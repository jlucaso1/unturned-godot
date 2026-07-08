using System.Collections.Generic;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;

namespace UnturnedGodot;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class Main : Node3D
{
    // Overridable via UNTURNED_PATH env var.
    private const string DefaultUnturnedPath =
        "/home/jlucaso/.local/share/Steam/steamapps/common/Unturned";

    private const string MapName = "PEI";

    public override void _Ready()
    {
        string unturnedPath = OS.GetEnvironment("UNTURNED_PATH");
        if (string.IsNullOrEmpty(unturnedPath))
            unturnedPath = DefaultUnturnedPath;

        string mapPath = System.IO.Path.Combine(unturnedPath, "Maps", MapName);
        var level = new LevelInfo(mapPath);

        GD.Print($"[unturned-godot] Loading map {level.Name} at {level.Path}");

        BuildTerrain(level);
        BuildObjects(level,
            System.IO.Path.Combine(unturnedPath, "Bundles", "Objects"),
            System.IO.Path.Combine(unturnedPath, "Bundles", "core_linux.masterbundle"));
        SetupEnvironment();

        if (DisplayServer.GetName() == "headless")
        {
            GD.Print("[unturned-godot] Headless: data loaded, quitting.");
            GetTree().Quit();
            return;
        }

        string shot = OS.GetEnvironment("SCREENSHOT_PATH");
        if (!string.IsNullOrEmpty(shot))
            _ = CaptureAndQuit(shot);
    }

    // Render a few frames before grabbing the framebuffer so meshes are actually drawn.
    private async System.Threading.Tasks.Task CaptureAndQuit(string path)
    {
        var cam = GetNode<FreeCamera>("FreeCamera");
        cam.Position = new Vector3(-256, 900, 700);
        cam.RotationDegrees = new Vector3(-55, -20, 0);

        for (int i = 0; i < 5; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        Image img = GetViewport().GetTexture().GetImage();
        img.SavePng(path);
        GD.Print($"[unturned-godot] Screenshot saved: {path}");
        GetTree().Quit();
    }

    private void BuildTerrain(LevelInfo level)
    {
        var tiles = level.EnumerateTiles();
        GD.Print($"[unturned-godot] Terrain tiles: {tiles.Count}");

        var terrainRoot = new Node3D { Name = "Terrain" };
        AddChild(terrainRoot);

        foreach (var (x, y) in tiles)
        {
            var tile = HeightmapTile.Read(level.HeightmapPath(x, y), x, y);
            var splat = SplatmapTile.TryRead(level.SplatmapPath(x, y), x, y);
            terrainRoot.AddChild(TerrainBuilder.BuildTile(tile, splat));
        }
    }

    private void BuildObjects(LevelInfo level, string objectBundlesDir, string bundlePath)
    {
        List<PlacedObject> objects = LevelObjects.Load(level.ObjectsDat);
        ObjectAssetDatabase db = ObjectAssetDatabase.ScanDirectory(objectBundlesDir);
        GD.Print($"[unturned-godot] Placed objects: {objects.Count}, asset db entries: {db.Count}");

        if (objects.Count == 0) return;

        string cacheDir = ProjectSettings.GlobalizePath("user://model_cache");
        var neededGuids = new HashSet<System.Guid>();
        foreach (PlacedObject o in objects)
            neededGuids.Add(o.Guid);

        // Parse the 1.4 GB bundle once, then reuse the compact per-GUID mesh cache.
        if (ModelLibrary.CachedMeshCount(cacheDir) == 0 && System.IO.File.Exists(bundlePath))
        {
            GD.Print("[unturned-godot] Extracting models from masterbundle (one-time)...");
            int extracted = ModelExtractor.Extract(bundlePath, objectBundlesDir, neededGuids, cacheDir);
            GD.Print($"[unturned-godot] Extracted {extracted} meshes to cache");
        }

        var meshLibrary = ModelLibrary.Load(cacheDir);
        Node3D objectsRoot = ObjectsBuilder.Build(objects, db, meshLibrary, out int withMesh);
        AddChild(objectsRoot);
        GD.Print($"[unturned-godot] Rendered {withMesh}/{objects.Count} objects with real meshes " +
            $"({meshLibrary.Count} unique)");
    }

    private void SetupEnvironment()
    {
        var sun = new DirectionalLight3D
        {
            Name = "Sun",
            RotationDegrees = new Vector3(-50, -30, 0),
            ShadowEnabled = true,
        };
        AddChild(sun);

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial() },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
        };
        AddChild(new WorldEnvironment { Environment = env, Name = "WorldEnvironment" });

        var camera = new FreeCamera { Name = "FreeCamera" };
        AddChild(camera);
        camera.Position = new Vector3(0, 300, 0); // above map center, looking down
        camera.RotationDegrees = new Vector3(-60, 0, 0);

        if (DisplayServer.GetName() != "headless")
            AddChild(new DebugOverlay { Name = "DebugOverlay" });
    }
}
