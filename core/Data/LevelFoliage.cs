using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace UnturnedGodot.Data;

// One foliage asset's instances inside a tile: the FoliageInstancedMeshInfoAsset GUID and, packed straight
// into the MultiMesh instance-buffer layout, the world transforms (already reflected from Unity to Godot
// space) at which to place its mesh. Each instance is 12 floats — the three basis rows interleaved with the
// origin component (m00 m10 m20 ox | m01 m11 m21 oy | m02 m12 m22 oz) — so FoliageBuilder can Array.Copy them
// into the MultiMesh buffer without ever materializing a Transform3D.
public sealed class FoliageInstances
{
    private const int Stride = 12;

    public Guid Asset { get; }
    public float[] Packed { get; }
    public int Count => Packed.Length / Stride;

    public FoliageInstances(Guid asset, float[] packed)
    {
        Asset = asset;
        Packed = packed;
    }

    // Reconstructs the i-th instance's Godot transform from the packed floats (for callers/tests that need a
    // Transform3D). Inverse of the packing in LevelFoliage.PackTransform: the Basis takes column vectors.
    public Transform3D InstanceTransform(int i)
    {
        int o = i * Stride;
        return new Transform3D(
            new Basis(
                new Vector3(Packed[o + 0], Packed[o + 4], Packed[o + 8]),
                new Vector3(Packed[o + 1], Packed[o + 5], Packed[o + 9]),
                new Vector3(Packed[o + 2], Packed[o + 6], Packed[o + 10])),
            new Vector3(Packed[o + 3], Packed[o + 7], Packed[o + 11]));
    }
}

// One 32 m foliage tile and the instanced-mesh foliage baked into it, grouped by asset.
public sealed class FoliageTile
{
    public int X { get; }
    public int Y { get; }
    public IReadOnlyList<FoliageInstances> Instances { get; }

    public FoliageTile(int x, int y, IReadOnlyList<FoliageInstances> instances)
    {
        X = x;
        Y = y;
        Instances = instances;
    }
}

// Ports Unturned's baked foliage (Foliage.blob, SDG.Framework.Foliage.FoliageStorageV2): the grass,
// flowers and pebbles instanced across the map. The file is a header — version, a per-tile table of blob
// offsets, and the list of foliage asset GUIDs — followed by each tile's instances: per asset, a run of
// 4x4 transform matrices (16 floats) each trailed by a "clear when baked" flag. Little-endian, matching
// .NET's BinaryReader. Handles the current on-disk version (2, which indexes the asset list) and the
// older per-instance-GUID version (1).
public sealed class LevelFoliage
{
    public const float TileSize = 32f; // FoliageSystem.TILE_SIZE

    private const int VersionAssetListHeader = 2; // FOLIAGE_FILE_VERSION_ADDED_ASSET_LIST_HEADER

    public int Version { get; }
    public IReadOnlyList<Guid> AssetGuids { get; }
    public IReadOnlyList<FoliageTile> Tiles { get; }

    private LevelFoliage(int version, IReadOnlyList<Guid> assetGuids, IReadOnlyList<FoliageTile> tiles)
    {
        Version = version;
        AssetGuids = assetGuids;
        Tiles = tiles;
    }

    public static LevelFoliage? Load(string blobPath) =>
        File.Exists(blobPath) ? Parse(File.ReadAllBytes(blobPath)) : null;

    // Parses directly over the byte[] with a cursor and explicit little-endian reads (BinaryPrimitives),
    // instead of wrapping a MemoryStream+BinaryReader — the blob is ~667k instances, so this avoids ~11M
    // virtual Stream.Read dispatches for byte-identical output.
    public static LevelFoliage Parse(byte[] data)
    {
        int pos = 0;
        int version = ReadInt32(data, ref pos);
        if (version is not (1 or VersionAssetListHeader))
            throw new NotSupportedException($"Unsupported Foliage.blob version {version}");

        int tileCount = ReadInt32(data, ref pos);
        var coords = new (int x, int y, long offset)[tileCount];
        for (int i = 0; i < tileCount; i++)
            coords[i] = (ReadInt32(data, ref pos), ReadInt32(data, ref pos), ReadInt64(data, ref pos));

        var assetGuids = new List<Guid>();
        if (version >= VersionAssetListHeader)
        {
            int assetCount = ReadInt32(data, ref pos);
            for (int i = 0; i < assetCount; i++)
                assetGuids.Add(ReadGuid(data, ref pos));
        }

        long tileBlobHeaderOffset = pos;

        // Parse each tile from its own independent blob region in parallel — data and assetGuids are only
        // read, and each tile writes to its own result slot, so no synchronization is needed. Tiles is then
        // assembled in coords order for a deterministic result regardless of completion order.
        var tileInstances = new List<FoliageInstances>?[tileCount];
        System.Threading.Tasks.Parallel.For(0, tileCount, i =>
        {
            int p = (int)(tileBlobHeaderOffset + coords[i].offset);
            List<FoliageInstances> instances = ReadTile(data, ref p, version, assetGuids);
            if (instances.Count > 0)
                tileInstances[i] = instances;
        });

        var tiles = new List<FoliageTile>();
        for (int i = 0; i < tileCount; i++)
            if (tileInstances[i] is { } instances)
                tiles.Add(new FoliageTile(coords[i].x, coords[i].y, instances));

        return new LevelFoliage(version, assetGuids, tiles);
    }

    private static List<FoliageInstances> ReadTile(byte[] data, ref int pos, int version, List<Guid> assetGuids)
    {
        int instanceCount = ReadInt32(data, ref pos);
        var result = new List<FoliageInstances>(instanceCount);
        for (int i = 0; i < instanceCount; i++)
        {
            Guid asset = version >= VersionAssetListHeader
                ? AssetForIndex(ReadInt32(data, ref pos), assetGuids)
                : ReadGuid(data, ref pos);

            int matrixCount = ReadInt32(data, ref pos);
            var packed = new float[matrixCount * 12];
            for (int m = 0; m < matrixCount; m++)
            {
                PackTransform(data, ref pos, packed, m * 12);
                pos++; // clearWhenBaked flag (editor-only bake bookkeeping)
            }
            if (matrixCount > 0)
                result.Add(new FoliageInstances(asset, packed));
        }
        return result;
    }

    private static Guid AssetForIndex(int index, List<Guid> assetGuids) =>
        index >= 0 && index < assetGuids.Count ? assetGuids[index] : Guid.Empty;

    // Reads a Unity column-major 4x4 (16 floats, element = row + col*4) and writes the 12 floats of the
    // Godot MultiMesh instance transform straight into dest at offset o (three basis rows interleaved with
    // the origin: m00 m10 m20 ox | m01 m11 m21 oy | m02 m12 m22 oz). Unity is left-handed with +Z forward,
    // Godot right-handed, so world space is the Z-negating reflection F = diag(1,1,-1): a transform M maps as
    // F*M*F, negating the basis' off-diagonal Z terms (m2, m6, m8, m9) and the origin Z (m14). Packing here
    // avoids ever constructing a Basis/Transform3D, which the profiler flagged as the per-instance cost.
    private static void PackTransform(byte[] data, ref int pos, float[] dest, int o)
    {
        Span<float> m = stackalloc float[16];
        for (int i = 0; i < 16; i++)
            m[i] = ReadSingle(data, ref pos);

        dest[o + 0] = m[0]; dest[o + 1] = m[4]; dest[o + 2] = -m[8]; dest[o + 3] = m[12];
        dest[o + 4] = m[1]; dest[o + 5] = m[5]; dest[o + 6] = -m[9]; dest[o + 7] = m[13];
        dest[o + 8] = -m[2]; dest[o + 9] = -m[6]; dest[o + 10] = m[10]; dest[o + 11] = -m[14];
    }

    private static int ReadInt32(byte[] d, ref int p)
    {
        int v = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
        p += 4;
        return v;
    }

    private static long ReadInt64(byte[] d, ref int p)
    {
        long v = BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(p));
        p += 8;
        return v;
    }

    private static float ReadSingle(byte[] d, ref int p)
    {
        float v = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(p));
        p += 4;
        return v;
    }

    private static Guid ReadGuid(byte[] d, ref int p)
    {
        var g = new Guid(d.AsSpan(p, 16));
        p += 16;
        return g;
    }
}
