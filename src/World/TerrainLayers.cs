using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// The splat layer textures of every landscape tile, resolved the way the game does it: Level.hierarchy
// names eight LandscapeMaterialAsset GUIDs per tile (in splatmap layer order), each asset points at a
// texture inside a master bundle, and that texture is pulled out of that bundle by container path.
//
// This replaces matching a fixed set of layer names (PEI's) against Terrain/Materials.unity3d, which only
// ever worked for the map those names came from: layers differ per map AND per tile (Germany uses 30
// distinct combinations across its 36 tiles), and a workshop map's layers live in its own mod bundle.
//
// The textures are small (64x64) and stored inline in a bundle's SerializedFile, so a cold resolve only
// decodes that prefix, and every texture is cached on disk by GUID afterwards.
public sealed class TerrainLayers
{
    // Phase 1's product: the pixels each tile paints with, in splatmap layer order, with Guid.Empty
    // standing for a slot the tile leaves unpainted. Deliberately NOT ImageTextures — Load runs on a
    // worker during the interactive load, and an ImageTexture is a RenderingServer resource.
    private readonly Dictionary<(int X, int Y), Guid[]> _tileMaterials = new();
    private readonly Dictionary<Guid, CachedTexture> _pixels = new();

    // Phase 2's product, filled by Realise on the main thread. A tile is here only once every layer it
    // paints has become a real texture.
    private readonly Dictionary<(int X, int Y), ImageTexture[]> _byTile = new();
    private bool _realised;

    public int TileCount => _byTile.Count;
    public int TextureCount => _pixels.Count;

    // The eight layer textures for a tile, or null when the tile's materials could not be resolved (the
    // caller then falls back to the averaged-color material). Only meaningful after Realise; before it,
    // every tile answers null, which is why the tile/LOD generation runs after Realise rather than beside
    // it — the "is this tile textured" answer it takes has to be the one FinishTile will take.
    public ImageTexture[]? For(int tileX, int tileY) =>
        _byTile.TryGetValue((tileX, tileY), out ImageTexture[]? layers) ? layers : null;

    // Phase 2 — MAIN THREAD ONLY: turn the resolved pixels into ImageTextures.
    //
    // This is split out rather than done in Load because Load is run on the thread pool by the
    // interactive terrain build, and Image.CreateFromData + ImageTexture.CreateFromImage create
    // RenderingServer resources — the same main-thread-only rule ModelLibrary.Realise, TerrainBuilder's
    // FinishTile and TextureRegistry.Apply all keep. Doing it inside Load put eight to thirty texture
    // creations on a worker, concurrently with the loading screen's own RenderingServer traffic.
    //
    // A layer whose format this build cannot turn into an Image (an undecodable crunched entry) drops its
    // whole tile back to the flat-colour fallback, which is exactly what the pre-split code did by
    // leaving that GUID out of the resolved set. Returns the number of tiles that ended up textured.
    public int Realise()
    {
        if (_realised)
            return _byTile.Count;
        _realised = true;

        var built = new Dictionary<Guid, ImageTexture>();
        ImageTexture? blank = null;
        foreach (((int x, int y) coord, Guid[] materials) in _tileMaterials)
        {
            var resolved = new ImageTexture[materials.Length];
            bool complete = true;
            for (int i = 0; i < materials.Length && complete; i++)
            {
                if (materials[i] == Guid.Empty)
                {
                    resolved[i] = blank ??= BlankLayer();
                    continue;
                }
                if (!built.TryGetValue(materials[i], out ImageTexture? texture))
                {
                    texture = ModelLibrary.BuildTexture(_pixels[materials[i]]);
                    if (texture != null)
                        built[materials[i]] = texture;
                }
                if (texture == null)
                    complete = false; // an unsupported format: this tile falls back to its averaged colour
                else
                    resolved[i] = texture;
            }

            if (complete)
                _byTile[coord] = resolved;
        }

        return _byTile.Count;
    }

    // Stands in for a layer slot the tile leaves empty. Black contributes nothing where the splat weight
    // is zero, and keeps an unexpected non-zero weight from smearing another layer's art across the tile.
    // A RenderingServer resource like any other, so it sits on Realise's side of the split.
    private static ImageTexture BlankLayer()
    {
        Image image = Image.CreateEmpty(1, 1, false, Image.Format.Rgb8);
        image.Fill(Colors.Black);
        return ImageTexture.CreateFromImage(image);
    }

    // Phase 1 — everything from here down runs on a worker thread during the interactive load, and so
    // touches no engine object at all. Above it is the main thread's half.
    //
    // `shared` is the object extraction pass, which decodes the very same bundles: whatever it produced
    // is taken from there instead of decoding them again. Null (the synchronous build) keeps the old
    // behaviour of resolving everything here.
    public static TerrainLayers Load(string unturnedPath, LevelInfo level,
        System.Threading.Tasks.Task<IReadOnlyDictionary<Guid, CachedTexture>>? shared = null)
    {
        var layers = new TerrainLayers();
        Dictionary<(int x, int y), Guid[]> tiles =
            LevelHierarchy.ReadTileMaterials(Path.Combine(level.Path, "Level.hierarchy"));
        if (tiles.Count == 0)
            return layers;

        // Landscape materials ship with the game and with any workshop mod that adds its own terrain art.
        IReadOnlyList<ContentSource> sources = ContentSource.Discover(unturnedPath);
        var materials = new Dictionary<Guid, LandscapeMaterialAsset>();
        var claimantRoots = new Dictionary<Guid, string>();
        foreach (ContentSource source in sources)
            LandscapeMaterialAsset.MergeFirstClaimants(materials, claimantRoots, source.Root,
                LandscapeMaterialAsset.ScanDirectory(Path.Combine(source.AssetsDir, "Landscapes")));

        if (materials.Count == 0)
            return layers;

        var needed = new HashSet<Guid>();
        foreach (Guid[] guids in tiles.Values)
            foreach (Guid guid in guids)
                if (guid != Guid.Empty && materials.ContainsKey(guid))
                    needed.Add(guid);

        Dictionary<Guid, CachedTexture> textures = ResolveTextures(sources, materials, claimantRoots, needed,
            shared);
        foreach ((Guid guid, CachedTexture texture) in textures)
            layers._pixels[guid] = texture;

        foreach (((int x, int y) coord, Guid[] guids) in tiles)
        {
            if (guids.Length < SplatmapTile.LAYERS)
                continue;

            var resolved = new Guid[SplatmapTile.LAYERS];
            bool complete = true;
            for (int i = 0; i < SplatmapTile.LAYERS && complete; i++)
            {
                if (guids[i] == Guid.Empty)
                {
                    // A tile only names as many layers as it paints; the rest are empty slots whose
                    // splat weight is zero. Germany leaves 29 of its 36 tiles partly empty, so treating
                    // that as "unresolved" cost those tiles their real layers entirely.
                    resolved[i] = Guid.Empty;
                }
                else if (textures.ContainsKey(guids[i]))
                {
                    resolved[i] = guids[i];
                }
                else
                {
                    complete = false; // a layer the tile does paint could not be read
                }
            }

            if (complete)
                layers._tileMaterials[coord] = resolved;
        }

        return layers;
    }

    // Cached entries first, then whatever the object pass decoded on its way through the same bundles, and
    // only then a pass of our own for anything still missing. Pixels only — see Realise for why.
    private static Dictionary<Guid, CachedTexture> ResolveTextures(IReadOnlyList<ContentSource> sources,
        Dictionary<Guid, LandscapeMaterialAsset> materials, IReadOnlyDictionary<Guid, string> claimantRoots,
        HashSet<Guid> needed,
        System.Threading.Tasks.Task<IReadOnlyDictionary<Guid, CachedTexture>>? shared)
    {
        var result = new Dictionary<Guid, CachedTexture>();
        var missing = new List<Guid>();
        Dictionary<string, TerrainLayerPlan.BundleWants> allWants =
            TerrainLayerPlan.ByBundle(needed, materials, claimantRoots, sources, MasterBundleConfig.Load);
        Dictionary<Guid, string> bundlePaths = TerrainLayerPlan.MaterialBundlePaths(allWants);

        foreach (Guid guid in needed)
        {
            if (bundlePaths.TryGetValue(guid, out string? bundlePath)
                && TerrainLayerCache.Read(guid, bundlePath) is { } cached)
                result[guid] = cached;
            else
                missing.Add(guid);
        }

        if (missing.Count > 0 && shared != null)
        {
            // The object pass is decoding these bundles anyway; waiting for it costs nothing beyond what
            // that pass already has to read, where decoding them again here cost this map twenty seconds.
            IReadOnlyDictionary<Guid, CachedTexture> produced = shared.Result;
            var stillMissing = new List<Guid>();
            foreach (Guid guid in missing)
            {
                if (produced.TryGetValue(guid, out CachedTexture texture))
                    result[guid] = texture;
                else
                    stillMissing.Add(guid);
            }
            missing = stillMissing;
        }

        if (missing.Count == 0)
            return result;

        // Group what is left by the bundle its material names, since one bundle decode serves many.
        Dictionary<string, TerrainLayerPlan.BundleWants> byBundle =
            TerrainLayerPlan.ByBundle(missing, materials, claimantRoots, sources, MasterBundleConfig.Load);

        foreach ((string bundlePath, TerrainLayerPlan.BundleWants bundle) in byBundle)
        {
            Dictionary<string, Guid[]> wanted = bundle.ByContainerPath;
            Log.Print($"[unturned-godot] Resolving {wanted.Count} terrain layer textures from "
                + $"{Path.GetFileName(bundlePath)}…");
            try
            {
                // One forward pass: the game's layer textures sit inline in the SerializedFile, so that
                // bundle stops right after its prefix, while a mod that keeps its terrain art in the .resS
                // stream is decoded only as far as the last texture this map asks for. Reading the prefix
                // and then the whole blob separately cost this map's cold load twenty seconds.
                Dictionary<string, CachedTexture> found =
                    BundleTextures.ExtractStreamed(bundlePath, wanted.Keys);

                foreach ((string containerPath, CachedTexture texture) in found)
                    foreach (Guid guid in wanted[containerPath])
                    {
                        TerrainLayerCache.Write(guid, texture, bundlePath);
                        result[guid] = texture;
                    }
            }
            catch (Exception e)
            {
                // An unreadable bundle costs this map its layer art, not its terrain.
                Log.PrintErr($"[unturned-godot] terrain layers from {Path.GetFileName(bundlePath)} "
                    + $"unavailable ({e.GetType().Name}: {e.Message}).");
            }
        }

        return result;
    }

}
