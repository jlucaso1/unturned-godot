#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;
using UnturnedGodot.Unity;

namespace UnturnedGodot.EditorTools;

// Builds a map into the scene being edited so it can be flown through in the editor's own 3D viewport,
// with no play session. Shared by the dock and the one-shot EditorScript.
//
// The preview is deliberately UNOWNED: nodes are added to the edited scene root without setting Owner, so
// the editor never writes them into the .tscn. That matters here — a built PEI is ~400 meshes and ~670k
// foliage instances read out of the player's own game install, none of which belongs in version control.
// Saving the scene keeps it as it was; closing or reloading it drops the preview.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class WorldPreview
{
    public const string RootName = "UnturnedPreview";

    // The install the preview reads from: UNTURNED_PATH, else Steam autodetection. Null when neither
    // resolves to a real directory.
    public static string? FindInstall()
    {
        string path = OS.GetEnvironment(UnturnedInstall.PathEnvironmentVariable);
        if (string.IsNullOrEmpty(path))
            path = UnturnedInstall.FindInstall(UnturnedInstall.DefaultSteamRoots()) ?? "";
        return Directory.Exists(path) ? path : null;
    }

    public static string CacheDir => ProjectSettings.GlobalizePath("user://model_cache");
    public static string TextureCacheDir => ProjectSettings.GlobalizePath("user://texture_cache");
    public static string TerrainCacheDir => ProjectSettings.GlobalizePath("user://terrain_cache");

    private sealed record CachePlan(List<ContentExtraction.BundlePlan> Bundles,
        Dictionary<string, TerrainLayerPlan.BundleWants> TerrainLayers, int Needed);

    // Restrictions on, substitutions OFF, for every path in this file. Level.cs:282 gates holiday
    // redirects on `isEditor == false`, so the editor shows a map's authored objects rather than the
    // seasonal ones it would swap in at runtime — and both halves here have to agree about that or they
    // fight: the dock's "Warm cache" would extract December's variants while the preview asked for the
    // originals and drew them as fallback boxes. Named once rather than spelled at each call site,
    // because the two disagreeing is the whole failure mode.
    private static HolidayPolicy EditorHolidays => HolidayPolicy.FromClock();

    // How much of a map's cache is already on disk. Mesh and texture misses are separate because textures
    // can be incomplete while every mesh GUID is ready, and they have different editor consequences.
    public static (int MissingMeshes, int MissingTextures, int MissingTerrainLayers, int Needed) CacheState(
        string unturnedPath, string mapPath)
    {
        CachePlan cachePlan = PlanFor(unturnedPath, mapPath);
        int missingMeshes = 0, missingTextures = 0;
        foreach (ContentExtraction.BundlePlan plan in cachePlan.Bundles)
        {
            missingMeshes += plan.Missing.Count;
            missingTextures += plan.MissingTextures.Count;
        }
        int missingTerrainLayers = TerrainLayerPlan.MaterialBundlePaths(cachePlan.TerrainLayers).Count;
        return (missingMeshes, missingTextures, missingTerrainLayers, cachePlan.Needed);
    }

    // What each bundle owes this map. Goes through the same planner the game does, so a workshop map is
    // measured against ITS bundle too rather than only the game's — the dock would otherwise call a map
    // fully cached while every one of its custom objects was still missing.
    private static CachePlan PlanFor(string unturnedPath, string mapPath)
    {
        IReadOnlyList<ContentSource> sources = ContentSource.Discover(unturnedPath);
        ObjectAssetDatabase assets = ContentExtraction.ScanAssets(sources);

        // Through the same resolve the build itself uses, so what the dock calls "cached" is measured
        // against the exact set the build will ask for — the vehicles rolled from the map's own tables
        // included, and the NPC characters excluded. Counted, they left the dock reporting forty meshes
        // permanently missing on Russia: an NPC ships no prefab, so nothing can ever extract one.
        // The blob's header alone is read for the foliage GUIDs; nothing here draws an instance.
        LevelContent content = LevelContentPlan.Resolve(sources, new LevelInfo(mapPath), assets,
            LevelFoliageChunks.ReadAssetGuids(Path.Combine(mapPath, "Foliage.blob")), EditorHolidays);

        Dictionary<string, TerrainLayerPlan.BundleWants> terrainLayers = MissingTerrainLayers(mapPath, sources);
        var bundlesOwingLayers = new HashSet<string>(terrainLayers.Keys, StringComparer.Ordinal);
        List<ContentExtraction.BundlePlan> bundles = ContentExtraction.Plan(sources, CacheDir,
            TextureCacheDir, content.NeededGuids, assets, content.FoliageAssets, bundlesOwingLayers);
        return new CachePlan(bundles, terrainLayers, content.NeededGuids.Count);
    }

    private static Dictionary<string, TerrainLayerPlan.BundleWants> MissingTerrainLayers(string mapPath,
        IReadOnlyList<ContentSource> sources)
    {
        Dictionary<(int x, int y), Guid[]> tiles =
            LevelHierarchy.ReadTileMaterials(Path.Combine(mapPath, "Level.hierarchy"));
        var materials = new Dictionary<Guid, LandscapeMaterialAsset>();
        var claimantRoots = new Dictionary<Guid, string>();
        foreach (ContentSource source in sources)
            LandscapeMaterialAsset.MergeFirstClaimants(materials, claimantRoots, source.Root,
                LandscapeMaterialAsset.ScanDirectory(Path.Combine(source.AssetsDir, "Landscapes")));

        var needed = new HashSet<Guid>();
        foreach (Guid[] guids in tiles.Values)
            foreach (Guid guid in guids)
                if (guid != Guid.Empty && materials.ContainsKey(guid))
                    needed.Add(guid);

        Dictionary<string, TerrainLayerPlan.BundleWants> all =
            TerrainLayerPlan.ByBundle(needed, materials, claimantRoots, sources, MasterBundleConfig.Load);
        Dictionary<Guid, string> owners = TerrainLayerPlan.MaterialBundlePaths(all);
        HashSet<Guid> missing = TerrainLayerCache.Missing(needed, owners, TerrainCacheDir);
        return TerrainLayerPlan.ByBundle(missing, materials, claimantRoots, sources, MasterBundleConfig.Load);
    }

    // Extracts everything `mapName` needs and is missing, straight into the shared cache. Pure parsing and
    // file IO — no Godot nodes or resources — so the caller can run it on a worker and keep the editor
    // responsive. Returns a one-line summary.
    public static string WarmCache(string unturnedPath, string mapName)
    {
        string mapPath = MapCatalog.ResolvePath(unturnedPath, mapName);
        CachePlan cachePlan = PlanFor(unturnedPath, mapPath);
        if (cachePlan.Bundles.Count == 0)
            return $"No content bundle found for {mapName}.";

        ObjectAssetDatabase db = ContentExtraction.ScanAssets(ContentSource.Discover(unturnedPath));
        int meshes = 0, textures = 0, terrainLayers = 0;
        foreach (ContentExtraction.BundlePlan plan in cachePlan.Bundles)
        {
            if (plan.NeedsExtraction)
            {
                meshes += ModelExtractor.ExtractMeshes(plan.Source, plan.Needed, CacheDir, db, plan.Foliage);
                textures += ModelExtractor.ExtractTextures(plan.Source.BundlePath, plan.Source.CacheTag,
                    CacheDir, TextureCacheDir);
            }

            if (cachePlan.TerrainLayers.TryGetValue(plan.Source.BundlePath,
                out TerrainLayerPlan.BundleWants? wants))
                foreach ((string path, CachedTexture texture) in
                    BundleTextures.ExtractStreamed(plan.Source.BundlePath, wants.ByContainerPath.Keys))
                    foreach (Guid material in wants.ByContainerPath[path])
                    {
                        TerrainLayerCache.Write(material, texture, plan.Source.BundlePath, TerrainCacheDir);
                        terrainLayers++;
                    }
        }

        return $"{mapName}: {meshes} meshes, {textures} object textures and "
            + $"{terrainLayers} terrain layers cached.";
    }

    // Builds the map under a single `UnturnedPreview` node and returns it detached, plus a per-subsystem
    // report. Each subsystem is caught on its own: maps whose per-map bundles use a SerializedFile version
    // this project cannot read yet still give a usable preview of everything else, instead of one throw
    // taking the whole build down.
    // What to put in the preview. Each of these exists because it is a distinct cost you may not want while
    // navigating: foliage is ~670k instances to draw, objects are the bulk of the build time, and a
    // directional shadow spanning 4 km of terrain is the heaviest single thing in the frame.
    public readonly record struct PreviewOptions(bool Objects, bool Foliage, bool Shadows)
    {
        public bool NeedsPlacements => Objects || Foliage;
        // Terrain, roads, water and sky, with objects but no foliage and no shadows: the combination that
        // is both quick to build and smooth to fly through.
        public static PreviewOptions Default => new(Objects: true, Foliage: false, Shadows: false);
    }

    // Builds the preview a slice at a time, handing the editor its frame back between slices, so the
    // window keeps redrawing instead of locking up for the whole build. The pure parsing runs on a worker;
    // everything that touches the RenderingServer stays on the calling (main) thread, yielding through the
    // same staged helpers the in-game loading screen uses.
    //
    // This deliberately never extracts from the masterbundle. A cold cache would be tens of seconds of
    // frozen editor with no way to cancel — the dock's "Warm cache" button does that in the background
    // instead, and anything still missing simply renders as a fallback box here.
    //
    // `yieldOn` must be inside the editor's scene tree (the dock itself is); `onStatus` is called on the
    // main thread before each slice.
    public static async System.Threading.Tasks.Task<Node3D> BuildAsync(string unturnedPath, string mapName,
        PreviewOptions options, Node yieldOn, Action<string> onStatus, List<string> report)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var root = new Node3D { Name = RootName };
        string mapPath = MapCatalog.ResolvePath(unturnedPath, mapName);
        string environmentDir = Path.Combine(mapPath, "Environment");
        var level = new LevelInfo(mapPath);

        LevelLighting? lighting = null;
        Try(report, "lighting", () =>
            lighting = LevelLighting.Load(Path.Combine(environmentDir, "Lighting.dat")));

        // Placements and the asset database are pure file parsing: start them on a worker so they overlap
        // the terrain build below, exactly as the game does.
        onStatus("Reading placements…");
        System.Threading.Tasks.Task<Placements>? placements = options.NeedsPlacements
            ? System.Threading.Tasks.Task.Run(() => ReadPlacements(unturnedPath, level, options.Foliage))
            : null;

        onStatus("Terrain…");
        HeightmapSampler? heights = null;
        try
        {
            (Node3D terrain, int tileCount, HeightmapSampler sampler) =
                await WorldBuilder.BuildTerrainAsync(unturnedPath, level, yieldOn);
            root.AddChild(terrain);
            heights = sampler;
            report.Add($"ok   terrain ({tileCount} tiles)");
        }
        catch (Exception e)
        {
            report.Add($"FAIL terrain: {e.GetType().Name}: {e.Message}");
        }

        if (placements != null)
        {
            Placements? loaded = null;
            try
            {
                loaded = await placements;
            }
            catch (Exception e)
            {
                report.Add($"FAIL placements: {e.GetType().Name}: {e.Message}");
            }

            if (loaded != null)
                await BuildObjectsAsync(root, loaded, options, yieldOn, onStatus, report);
        }

        onStatus("Roads, water and sky…");
        await Yield(yieldOn);

        if (heights is { } sampled)
            Try(report, "roads", () => root.AddChild(RoadsBuilder.Build(environmentDir, sampled, unturnedPath)));

        StandardMaterial3D? water = null;
        Try(report, "water", () =>
        {
            root.AddChild(WaterBuilder.Build(lighting, out StandardMaterial3D mat));
            water = mat;
        });
        Try(report, "nodes", () => root.AddChild(NodesBuilder.Build(environmentDir)));
        Try(report, "sky + sun", () =>
        {
            Node sky = DayNightController.Build(lighting, water ?? new StandardMaterial3D(),
                SkyboxAssets.Load(unturnedPath));
            if (!options.Shadows)
                DisableShadows(sky);
            root.AddChild(sky);
        });

        report.Add($"built in {timer.ElapsedMilliseconds} ms");
        return root;
    }

    // The map's placed objects plus everything needed to turn them into nodes, all read off disk. The
    // foliage rides alongside the LevelContent rather than inside it: LevelContentPlan resolves which
    // foliage ASSETS a map needs, and this is the scattered instance data the preview then draws.
    private sealed record Placements(LevelContent Content, LevelFoliageChunks? Foliage);

    // Deliberately nothing but the two reads and the shared resolve. This used to be its own copy of the
    // sequence and had drifted into three visible bugs — the NPCs were never partitioned out, so the
    // preview drew Russia's forty characters as placeholder boxes and re-chased their GUIDs through the
    // extraction plan on every preview; the needed set came from the raw placement GUIDs, so the
    // stale-GUID-to-legacy-id fallback never ran; and the foliage GUIDs went in unresolved.
    private static Placements ReadPlacements(string unturnedPath, LevelInfo level, bool drawFoliage)
    {
        IReadOnlyList<ContentSource> sources = ContentSource.Discover(unturnedPath);
        ObjectAssetDatabase db = ContentExtraction.ScanAssets(sources);

        // The chunked loader, which is what the game builds its own MultiMeshes from — LevelFoliage.Load,
        // which this read before, hands back a flat instance list, and a preview that draws its grass
        // differently from the session is not previewing the session. Deliberately NOT the residency
        // index the game prefers: that one builds a FoliageStreamingRenderer which pages chunks in around
        // a player camera each frame, and the preview is flown with the editor's camera, which nothing
        // here drives.
        //
        // The instances are read only when they are going to be drawn. The GUIDs still are either way —
        // they belong in the needed set so the dock's cache report and the mesh library agree with a real
        // load — but those live in the blob's header, so a preview with foliage off no longer pulls tens
        // of megabytes of scatter data through to throw it away.
        string blobPath = Path.Combine(level.Path, "Foliage.blob");
        LevelFoliageChunks? foliage = drawFoliage
            ? LevelFoliageChunks.Load(blobPath, FoliageBuilder.RuntimeChunkTiles)
            : null;
        IReadOnlyList<Guid>? foliageGuids = foliage?.AssetGuids
            ?? LevelFoliageChunks.ReadAssetGuids(blobPath);

        // The vehicle roll inside is seeded, so the preview shows the same vehicles a session would.
        return new Placements(
            LevelContentPlan.Resolve(sources, level, db, foliageGuids, EditorHolidays), foliage);
    }

    private static async System.Threading.Tasks.Task BuildObjectsAsync(Node3D root, Placements placements,
        PreviewOptions options, Node yieldOn, Action<string> onStatus, List<string> report)
    {
        onStatus("Meshes…");
        var registry = new TextureRegistry(TextureCacheDir);
        Dictionary<Guid, ArrayMesh> meshLibrary;
        try
        {
            // Staged: realises the ArrayMeshes in batches, yielding to the editor's render loop between
            // them. This is the slice that would otherwise stall the window the longest.
            meshLibrary = await ModelLibrary.LoadStagedAsync(CacheDir, registry, yieldOn,
                placements.Content.NeededGuids);
        }
        catch (Exception e)
        {
            report.Add($"FAIL meshes: {e.GetType().Name}: {e.Message}");
            return;
        }

        // The prefabs' own lower levels, staged like the base library above. Without these the preview
        // would draw LOD-0 at every distance and so stop matching the scene a session actually renders —
        // which is most of what a preview is for. Skipped entirely when the feature is off, so the flag
        // costs nothing here either.
        // Objects are their own preview toggle, and only the objects block below reads this: a
        // foliage-only preview would otherwise realise a whole library it never draws.
        Dictionary<Guid, ArrayMesh>? lod1Library = null;
        if (options.Objects && ObjectsBuilder.ObjectLodEnabled)
            try
            {
                lod1Library = await ModelLibrary.LoadStagedAsync(CacheDir, registry, yieldOn,
                    placements.Content.NeededGuids, ModelExtractor.Lod1Suffix);
            }
            catch (Exception e)
            {
                report.Add($"  lower levels unavailable: {e.GetType().Name}: {e.Message}");
            }

        onStatus("Objects…");
        await Yield(yieldOn);

        // No collider library: nothing simulates in the editor, so the shapes and their BVH are pure cost.
        var noColliders = new Dictionary<Guid, List<CachedCollider>>();
        LevelContent content = placements.Content;
        if (options.Objects)
            Try(report, "objects", () =>
            {
                root.AddChild(ObjectsBuilder.Build(content.Objects, content.Db, meshLibrary, noColliders,
                    out int withMesh, lod1Library));
                int missing = content.Objects.Count - withMesh;
                report.Add($"  {withMesh}/{content.Objects.Count} objects, {meshLibrary.Count} unique meshes" +
                    (missing > 0 ? $" ({missing} as fallback boxes — warm the cache to fill them in)" : ""));

                // Under the objects toggle, because that is what they are here: static placements sharing
                // the same library, the same batching and the same fallback boxes.
                root.AddChild(WorldBuilder.BuildVehicles(content.Vehicles, content.Db, meshLibrary,
                    noColliders, lod1Library));
                report.Add($"  {content.Vehicles.Count} vehicles");

                // Said rather than silently dropped. An NPC ships no prefab of its own — it is the player
                // rig wearing what its .dat names — and building that here would mean importing the
                // character resources the preview deliberately never extracts. What it is NOT any more is
                // forty placeholder boxes and forty GUIDs the cache reports missing on every preview.
                if (content.Npcs.Count > 0)
                    report.Add($"  {content.Npcs.Count} NPC characters not drawn (no prefab; see NpcPlacements)");
            });

        if (options.Foliage)
        {
            onStatus("Foliage…");
            await Yield(yieldOn);
            Try(report, "foliage", () => root.AddChild(FoliageBuilder.Build(placements.Foliage, meshLibrary)));
        }

        onStatus("Textures…");
        await Yield(yieldOn);
        Try(report, "textures", () => report.Add($"  {registry.ApplyAllAvailable()} textures applied"));
    }

    private static async System.Threading.Tasks.Task Yield(Node yieldOn) =>
        await yieldOn.ToSignal(yieldOn.GetTree(), SceneTree.SignalName.ProcessFrame);

    // A directional light shadowing several km of terrain is the single most expensive thing in the
    // preview's frame; off by default so navigating stays smooth.
    private static void DisableShadows(Node node)
    {
        if (node is Light3D light)
            light.ShadowEnabled = false;
        foreach (Node child in node.GetChildren())
            DisableShadows(child);
    }

    private static void Try(List<string> report, string what, Action action)
    {
        try
        {
            action();
            report.Add($"ok   {what}");
        }
        catch (Exception e)
        {
            report.Add($"FAIL {what}: {e.GetType().Name}: {e.Message}");
        }
    }

    // Replaces any preview already under `parent` with a freshly built one. Renaming the old root before
    // freeing it keeps the name free for the replacement (QueueFree only frees at the end of the frame).
    public static Node3D Attach(Node parent, Node3D preview)
    {
        Clear(parent);
        parent.AddChild(preview);
        // Owner stays null on purpose: the editor only serializes owned nodes, so the preview can never be
        // saved into the scene file.
        return preview;
    }

    public static bool Clear(Node parent)
    {
        if (parent.GetNodeOrNull<Node3D>(RootName) is not { } existing)
            return false;
        existing.Name = RootName + "_freeing";
        existing.QueueFree();
        return true;
    }
}

#endif
