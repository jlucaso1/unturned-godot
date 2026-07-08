using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;

namespace UnturnedGodot;

// Streams the object world on a cold load: extract meshes (fast — SerializedFile only) on a worker, build
// the untextured scene so the map is playable in ~3 s, then extract textures (the ~1.18 GB .resS) on a
// worker and hot-swap them into the live materials as they land. A warm cache short-circuits to a normal
// synchronous build. All Godot node/resource work happens on the main thread (via Callable.CallDeferred);
// workers only do pure parsing/IO and hand texture keys back through a concurrent queue.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class ObjectStreamer : Node
{
    [Signal] public delegate void MeshesReadyEventHandler(double elapsedMs);
    [Signal] public delegate void ProgressEventHandler(int applied, int total);
    [Signal] public delegate void FinishedEventHandler();

    private const int MaxAppliesPerFrame = 8; // pace GPU uploads so streaming doesn't hitch the frame

    private string _objectBundlesDir = "";
    private string _treeBundlesDir = "";
    private string _assetsDir = "";
    private string _bundlePath = "";
    private string _cacheDir = "";
    private string _textureCacheDir = "";
    private LevelInfo _level = null!;

    private TextureRegistry _registry = null!;
    private List<PlacedObject> _objects = new();
    private ObjectAssetDatabase _db = null!;
    private Dictionary<Guid, FoliageAsset> _foliageAssets = new();
    private LevelFoliage? _foliage;

    private readonly ConcurrentQueue<string> _readyKeys = new();
    private int _totalTextureKeys;
    private int _appliedTextures;
    private volatile bool _texturesDone;
    private bool _sceneBuilt;
    private bool _drainedFinal;
    private Stopwatch _cold = new();

    public void Begin(string unturnedPath, LevelInfo level)
    {
        _level = level;
        _objectBundlesDir = Path.Combine(unturnedPath, "Bundles", "Objects");
        _treeBundlesDir = Path.Combine(unturnedPath, "Bundles", "Trees");
        _bundlePath = Path.Combine(unturnedPath, "Bundles", "core_linux.masterbundle");
        _assetsDir = Path.Combine(Path.GetDirectoryName(_bundlePath)!, "Assets");
        _cacheDir = ProjectSettings.GlobalizePath("user://model_cache");
        _textureCacheDir = ProjectSettings.GlobalizePath("user://texture_cache");
        _registry = new TextureRegistry(_textureCacheDir);

        LoadPlacements();

        // Cold-load if the object meshes aren't cached, or if the foliage meshes were never extracted
        // (e.g. an older cache from before foliage support) — both come from the same decode pass.
        bool foliageMissing = _foliageAssets.Keys.Any(
            g => !File.Exists(Path.Combine(_cacheDir, g.ToString("N") + ".mesh")));
        bool cold = (ModelLibrary.CachedMeshCount(_cacheDir) == 0 || foliageMissing) && File.Exists(_bundlePath);
        SetProcess(cold);
        if (cold)
            StartStreaming();
        else
            BuildAndFinish();
    }

    private void LoadPlacements()
    {
        _objects = LevelObjects.Load(_level.ObjectsDat);
        List<PlacedTree> trees = LevelTrees.Load(Path.Combine(_level.Path, "Terrain", "Trees.dat"));
        foreach (PlacedTree t in trees)
            _objects.Add(new PlacedObject(t.Position, t.EulerDegrees, t.Scale, 0, t.Guid));

        _db = ObjectAssetDatabase.ScanDirectory(_objectBundlesDir);
        foreach (ObjectAsset a in ObjectAssetDatabase.ScanDirectory(_treeBundlesDir).All)
            _db.Add(a);

        _foliage = LevelFoliage.Load(Path.Combine(_level.Path, "Foliage.blob"));
        if (_foliage != null)
            _foliageAssets = FoliageAsset.ScanForGuids(_assetsDir, new HashSet<Guid>(_foliage.AssetGuids));
    }

    // Warm path: meshes and textures are already cached — build synchronously and apply all textures now.
    private void BuildAndFinish()
    {
        BuildObjects();
        _registry.ApplyAllAvailable();
        EmitSignal(SignalName.MeshesReady, 0.0);
        EmitSignal(SignalName.Finished);
    }

    private void BuildObjects()
    {
        var meshLibrary = ModelLibrary.Load(_cacheDir, _registry);
        Node3D root = ObjectsBuilder.Build(_objects, _db, meshLibrary, out int withMesh);
        AddChild(root);
        AddChild(FoliageBuilder.Build(_foliage, meshLibrary));
        _totalTextureKeys = _registry.PendingKeyCount;
        GD.Print($"[stream] built {withMesh}/{_objects.Count} objects ({meshLibrary.Count} meshes), " +
            $"{_totalTextureKeys} texture keys pending");

        // These parsed inputs are consumed only up to here — the MultiMesh buffers now hold their own
        // copies and the streaming worker already captured _db by value. Drop them so the ~32 MB foliage
        // transform graph and the placement/asset lists don't live on this node for the whole session.
        _foliage = null;
        _objects = null!;
        _db = null!;
    }

    private void StartStreaming()
    {
        GD.Print("[stream] cold load: streaming meshes then textures from one decode pass...");
        _cold = Stopwatch.StartNew();
        var needed = new HashSet<Guid>();
        foreach (PlacedObject o in _objects)
            needed.Add(o.Guid);

        Task.Run(() =>
        {
            try
            {
                ModelExtractor.StreamExtract(_bundlePath, _objectBundlesDir, _treeBundlesDir, _assetsDir,
                    needed, _cacheDir, _textureCacheDir, _db,
                    onMeshesReady: () => Callable.From(OnMeshesExtracted).CallDeferred(),
                    onTextureWritten: key => _readyKeys.Enqueue(key),
                    foliageAssets: _foliageAssets.Values.ToList());
            }
            catch (Exception e)
            {
                Callable.From(() => GD.PrintErr($"[stream] extraction failed: {e}")).CallDeferred();
            }
            _texturesDone = true;
        });
    }

    // Main thread: meshes are cached — build the (untextured) scene. Textures keep streaming in on the
    // worker; _Process applies them (once this registry exists) as their keys land in the queue.
    private void OnMeshesExtracted()
    {
        double meshMs = _cold.Elapsed.TotalMilliseconds;
        BuildObjects();
        _sceneBuilt = true;
        EmitSignal(SignalName.MeshesReady, meshMs);
        GD.Print($"[stream] playable in {meshMs:0} ms (untextured); textures streaming in...");
    }

    public override void _Process(double delta)
    {
        if (!_sceneBuilt)
            return;

        int applied = 0;
        while (applied < MaxAppliesPerFrame && _readyKeys.TryDequeue(out string? key))
            if (_registry.Apply(key))
            {
                _appliedTextures++;
                applied++;
            }

        if (applied > 0)
            EmitSignal(SignalName.Progress, _appliedTextures, _totalTextureKeys);

        if (_texturesDone && _readyKeys.IsEmpty && !_drainedFinal)
        {
            _drainedFinal = true;
            _registry.ApplyAllAvailable(); // catch-up for any keys never signaled
            GD.Print($"[stream] fully textured in {_cold.Elapsed.TotalMilliseconds:0} ms " +
                $"({_appliedTextures}/{_totalTextureKeys} keys)");
            EmitSignal(SignalName.Finished);
            SetProcess(false);
        }
    }
}
