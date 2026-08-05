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
    private readonly Dictionary<(int X, int Y), ImageTexture[]> _byTile = new();

    public int TileCount => _byTile.Count;
    public int TextureCount { get; private set; }

    // The eight layer textures for a tile, or null when the tile's materials could not be resolved (the
    // caller then falls back to the averaged-color material).
    public ImageTexture[]? For(int tileX, int tileY) =>
        _byTile.TryGetValue((tileX, tileY), out ImageTexture[]? layers) ? layers : null;

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

        Dictionary<Guid, ImageTexture> textures = ResolveTextures(sources, materials, claimantRoots, needed,
            shared);
        layers.TextureCount = textures.Count;

        ImageTexture? unused = null;
        foreach (((int x, int y) coord, Guid[] guids) in tiles)
        {
            if (guids.Length < SplatmapTile.LAYERS)
                continue;

            var resolved = new ImageTexture[SplatmapTile.LAYERS];
            bool complete = true;
            for (int i = 0; i < SplatmapTile.LAYERS && complete; i++)
            {
                if (guids[i] == Guid.Empty)
                {
                    // A tile only names as many layers as it paints; the rest are empty slots whose
                    // splat weight is zero. Germany leaves 29 of its 36 tiles partly empty, so treating
                    // that as "unresolved" cost those tiles their real layers entirely.
                    resolved[i] = unused ??= BlankLayer();
                }
                else if (textures.TryGetValue(guids[i], out ImageTexture? texture))
                {
                    resolved[i] = texture;
                }
                else
                {
                    complete = false; // a layer the tile does paint could not be read
                }
            }

            if (complete)
                layers._byTile[coord] = resolved;
        }

        return layers;
    }

    // Cached entries first, then whatever the object pass decoded on its way through the same bundles, and
    // only then a pass of our own for anything still missing.
    private static Dictionary<Guid, ImageTexture> ResolveTextures(IReadOnlyList<ContentSource> sources,
        Dictionary<Guid, LandscapeMaterialAsset> materials, IReadOnlyDictionary<Guid, string> claimantRoots,
        HashSet<Guid> needed,
        System.Threading.Tasks.Task<IReadOnlyDictionary<Guid, CachedTexture>>? shared)
    {
        var result = new Dictionary<Guid, ImageTexture>();
        var missing = new List<Guid>();
        Dictionary<string, TerrainLayerPlan.BundleWants> allWants =
            TerrainLayerPlan.ByBundle(needed, materials, claimantRoots, sources, MasterBundleConfig.Load);
        Dictionary<Guid, string> bundlePaths = TerrainLayerPlan.MaterialBundlePaths(allWants);

        foreach (Guid guid in needed)
        {
            if (bundlePaths.TryGetValue(guid, out string? bundlePath)
                && TerrainLayerCache.Read(guid, bundlePath) is { } cached
                && ModelLibrary.BuildTexture(cached) is { } image)
                result[guid] = image;
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
                if (produced.TryGetValue(guid, out CachedTexture texture)
                    && ModelLibrary.BuildTexture(texture) is { } image)
                {
                    result[guid] = image;
                }
                else
                {
                    stillMissing.Add(guid);
                }
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
                {
                    ImageTexture? image = ModelLibrary.BuildTexture(texture);
                    foreach (Guid guid in wanted[containerPath])
                    {
                        TerrainLayerCache.Write(guid, texture, bundlePath);
                        if (image != null)
                            result[guid] = image;
                    }
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

    // Stands in for a layer slot the tile leaves empty. Black contributes nothing where the splat weight
    // is zero, and keeps an unexpected non-zero weight from smearing another layer's art across the tile.
    private static ImageTexture BlankLayer()
    {
        Image image = Image.CreateEmpty(1, 1, false, Image.Format.Rgb8);
        image.Fill(Colors.Black);
        return ImageTexture.CreateFromImage(image);
    }
}
