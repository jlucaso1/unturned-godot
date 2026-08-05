using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;

namespace UnturnedGodot;

// Scatters the map's baked foliage (Foliage.blob): grass, flowers and pebbles instanced from real
// masterbundle meshes. Instances are grouped into chunks of foliage tiles so each MultiMesh keeps a
// compact AABB Godot frustum-culls on its own — only the foliage in view is submitted, which matters
// with PEI's ~667k instances.
public static class FoliageBuilder
{
    // Four 32 m tiles keep culling bounds compact. Chunk nodes are centred on their actual transforms
    // below, and their visibility range includes the chunk + mesh radius so no nearby instance is dropped
    // merely because the aggregate AABB's centre is farther away. The environment override is retained for
    // repeatable profiling on other content.
    private static readonly int ChunkTiles = EnvInt("UG_FOLIAGE_CHUNK_TILES", 4, 1, 32);

    // Grass/flowers/pebbles are only meaningful up close (Unturned itself fades ground detail out at a
    // short distance). Beyond this, each chunk stops rendering, so the many chunks that carpet the
    // whole island no longer cost draw calls / primitives from elevated or distant views.
    private static readonly float DrawDistance = EnvFloat("UG_FOLIAGE_DISTANCE", 160f, 32f, 1000f);
    private static readonly float FadeMargin = Math.Min(32f, DrawDistance * 0.25f);
    private static readonly int PackBatchChunks = EnvInt("UG_FOLIAGE_PACK_BATCH", 256, 1, 65536);
    public static int RuntimeChunkTiles => ChunkTiles;
    public static float FadeMarginValue => FadeMargin;
    public static bool SpatialResidencyEnabled =>
        EnvFlag.IsOn(System.Environment.GetEnvironmentVariable("UG_FOLIAGE_RESIDENCY"), whenUnset: true)
        && !EnvFlag.IsOn(System.Environment.GetEnvironmentVariable("UG_NODE_MULTIMESH"), whenUnset: false);

    public static Node3D Build(FoliageResidencyIndex? foliage,
        IReadOnlyDictionary<Guid, ArrayMesh> meshLibrary) => foliage == null
            ? new Node3D { Name = "Foliage" }
            : FoliageStreamingRenderer.Create(foliage, meshLibrary, DrawDistance);

    public static Node3D Build(LevelFoliageChunks? foliage,
        IReadOnlyDictionary<Guid, ArrayMesh> meshLibrary)
    {
        bool nodeWrappers = EnvFlag.IsOn(OS.GetEnvironment("UG_NODE_MULTIMESH"), whenUnset: false);
        Node3D root = nodeWrappers
            ? new Node3D { Name = "Foliage" }
            : new MultiMeshRidRenderer { Name = "Foliage" };
        if (foliage == null) return root;
        var rebaseWatch = System.Diagnostics.Stopwatch.StartNew();
        foliage.RebaseAll(EnvFlag.IsOn(OS.GetEnvironment("UG_PARALLEL_FOLIAGE_REBASE"), whenUnset: true));
        double rebaseMs = rebaseWatch.Elapsed.TotalMilliseconds;
        int total = 0, renderedChunks = 0;
        foreach (FoliageChunk chunk in foliage.Chunks)
        {
            if (!meshLibrary.TryGetValue(chunk.Key.Asset, out ArrayMesh? mesh)) continue;
            float meshRadius = mesh.GetAabb().Size.Length() * 0.5f * MathF.Sqrt(chunk.Bounds.MaxScaleSquared);
            var multimesh = new MultiMesh
            {
                Mesh = mesh,
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                InstanceCount = chunk.Count,
                Buffer = chunk.Packed,
            };
            float visibilityEnd = DrawDistance + (chunk.Bounds.Max - chunk.Bounds.Min).Length() * 0.5f
                + meshRadius;
            if (root is MultiMeshRidRenderer rid)
                rid.Add(multimesh, new Transform3D(Basis.Identity, chunk.Origin), shadows: false,
                    visibilityEnd: visibilityEnd, visibilityMargin: FadeMargin);
            else
                root.AddChild(new MultiMeshInstance3D
                {
                    Multimesh = multimesh,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    VisibilityRangeEnd = visibilityEnd,
                    VisibilityRangeEndMargin = FadeMargin,
                    VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
                    Position = chunk.Origin,
                });
            total += chunk.Count;
            renderedChunks++;
        }
        Log.Print($"[unturned-godot] Foliage: {total} instances in {renderedChunks} chunks " +
            $"({foliage.AssetGuids.Count} asset types, {ChunkTiles * 32} m chunks, direct buffers, " +
            $"{DrawDistance:0} m range, rebase {rebaseMs:0.0} ms)");
        return root;
    }

    // Takes the already-parsed foliage (the caller loads Foliage.blob once for its asset GUIDs) rather
    // than re-reading the 43 MB / 667k-instance blob from disk a second time.
    public static Node3D Build(LevelFoliage? foliage, IReadOnlyDictionary<Guid, ArrayMesh> meshLibrary)
    {
        var root = new Node3D { Name = "Foliage" };
        if (foliage == null)
            return root;

        // Group the FoliageInstances by chunk as references, not by copying their 667k transforms into new
        // Lists — the transforms stay in the parsed foliage pool and are read straight into each chunk's GPU
        // buffer below, saving a full ~32 MB transient duplication.
        var availableAssets = new HashSet<Guid>(meshLibrary.Keys);
        FoliageGroups groups = FoliageGroups.Build(foliage.Tiles, ChunkTiles, availableAssets);

        // Build each chunk's packed instance buffer on worker threads — a pure Array.Copy of the parsed
        // floats into the MultiMesh buffer layout. Creating the MultiMesh, assigning its Buffer and AddChild
        // are RenderingServer ops, so they stay on this (main) thread. Arrays (not a List) hold the parallel
        // results so different indices are written without synchronization.
        int total = 0;
        for (int first = 0; first < groups.Count; first += PackBatchChunks)
        {
            int countInBatch = Math.Min(PackBatchChunks, groups.Count - first);
            var packedChunks = new PackedChunk[countInBatch];
            System.Threading.Tasks.Parallel.For(0, countInBatch,
                i => packedChunks[i] = PackBuffer(groups.Runs,
                    groups.Starts[first + i], groups.Starts[first + i + 1]));

            for (int inBatch = 0; inBatch < countInBatch; inBatch++)
            {
                int i = first + inBatch;
                FoliageGroupKey key = groups.Keys[i];
                (int cx, int cy, Guid asset) = (key.ChunkX, key.ChunkY, key.Asset);
                PackedChunk packed = packedChunks[inBatch];
                float[] buffer = packed.Buffer;
                int count = buffer.Length / 12;
                ArrayMesh mesh = meshLibrary[asset];
                float meshRadius = mesh.GetAabb().Size.Length() * 0.5f * packed.MaxScale;
                var multimesh = new MultiMesh
                {
                    Mesh = mesh,
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    InstanceCount = count,
                    Buffer = buffer,
                };

                root.AddChild(new MultiMeshInstance3D
                {
                    Multimesh = multimesh,
                    Name = $"F{cx}_{cy}_{asset:N}",
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, // foliage doesn't self-shadow
                    // Godot measures visibility range from the transformed aggregate AABB centre. Expanding
                    // by a conservative bound guarantees the intended per-instance fade range at chunk edges.
                    VisibilityRangeEnd = DrawDistance + packed.PositionRadius + meshRadius,
                    VisibilityRangeEndMargin = FadeMargin,
                    VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
                    Position = packed.Origin,
                });
                total += count;
            }
        }

        Log.Print($"[unturned-godot] Foliage: {total} instances in {groups.Count} chunks " +
            $"({foliage.AssetGuids.Count} asset types, {ChunkTiles * 32} m chunks, pack batch {PackBatchChunks}, " +
            $"{DrawDistance:0} m range)");
        return root;
    }

    private static int EnvInt(string name, int fallback, int min, int max) =>
        int.TryParse(System.Environment.GetEnvironmentVariable(name), out int value)
            ? Math.Clamp(value, min, max)
            : fallback;

    private static float EnvFloat(string name, float fallback, float min, float max) =>
        float.TryParse(System.Environment.GetEnvironmentVariable(name),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float value)
            ? Math.Clamp(value, min, max)
            : fallback;

    // Concatenates a chunk's grouped instances into one MultiMesh instance buffer with a bulk Array.Copy per
    // run — FoliageInstances already hold their transforms in the exact 12-float buffer layout, so no
    // per-instance Transform3D read is needed. Pure CPU (no Godot resource touched): safe on a worker thread.
    private readonly record struct PackedChunk(float[] Buffer, Vector3 Origin, float PositionRadius,
        float MaxScale);

    private static PackedChunk PackBuffer(FoliageInstances[] instances, int start, int end)
    {
        int count = 0;
        FoliageBounds bounds = FoliageBounds.Empty;
        for (int i = start; i < end; i++)
        {
            FoliageInstances inst = instances[i];
            count += inst.Count;
            bounds = bounds.Include(inst.Bounds);
        }

        var buffer = new float[count * 12];
        int o = 0;
        for (int i = start; i < end; i++)
        {
            FoliageInstances inst = instances[i];
            Array.Copy(inst.Packed, 0, buffer, o, inst.Packed.Length);
            o += inst.Packed.Length;
        }

        // MultiMesh transforms are relative to their GeometryInstance node. Keeping every transform in
        // world space while every node sat at (0,0,0) made VisibilityRangeEnd measure the map origin, not
        // this chunk — foliage kilometres away stayed eligible whenever the player was near map centre.
        // Rebase around the chunk's actual transform bounds: node * local remains byte-for-byte the same
        // world placement, while range culling and floating-point precision finally become spatial.
        Vector3 min = bounds.Min;
        Vector3 max = bounds.Max;
        Vector3 origin = (min + max) * 0.5f;
        for (int i = 0; i < buffer.Length; i += 12)
        {
            buffer[i + 3] -= origin.X;
            buffer[i + 7] -= origin.Y;
            buffer[i + 11] -= origin.Z;
        }
        return new PackedChunk(buffer, origin, (max - min).Length() * 0.5f,
            MathF.Sqrt(bounds.MaxScaleSquared));
    }
}
