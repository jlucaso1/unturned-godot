using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace UnturnedGodot.Data;

// One foliage asset's instances inside a tile: the FoliageInstancedMeshInfoAsset GUID and the world
// transforms (already converted from Unity to Godot space) at which to place its mesh.
public sealed class FoliageInstances
{
    public Guid Asset { get; }
    public IReadOnlyList<Transform3D> Transforms { get; }

    public FoliageInstances(Guid asset, IReadOnlyList<Transform3D> transforms)
    {
        Asset = asset;
        Transforms = transforms;
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

        var tiles = new List<FoliageTile>();
        foreach ((int x, int y, long offset) in coords)
        {
            pos = (int)(tileBlobHeaderOffset + offset);
            List<FoliageInstances> instances = ReadTile(data, ref pos, version, assetGuids);
            if (instances.Count > 0)
                tiles.Add(new FoliageTile(x, y, instances));
        }

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
            var transforms = new List<Transform3D>(matrixCount);
            for (int m = 0; m < matrixCount; m++)
            {
                transforms.Add(ReadTransform(data, ref pos));
                pos++; // clearWhenBaked flag (editor-only bake bookkeeping)
            }
            if (matrixCount > 0)
                result.Add(new FoliageInstances(asset, transforms));
        }
        return result;
    }

    private static Guid AssetForIndex(int index, List<Guid> assetGuids) =>
        index >= 0 && index < assetGuids.Count ? assetGuids[index] : Guid.Empty;

    // Reads a Unity column-major 4x4 (16 floats, element = row + col*4) and converts it to a Godot
    // Transform3D. Unity is left-handed with +Z forward; Godot is right-handed, so world space is the
    // Z-negating reflection F = diag(1,1,-1). A transform M maps the same way in both frames as F*M*F,
    // which negates the off-diagonal Z terms of the basis and the Z of the origin.
    private static Transform3D ReadTransform(byte[] data, ref int pos)
    {
        Span<float> m = stackalloc float[16];
        for (int i = 0; i < 16; i++)
            m[i] = ReadSingle(data, ref pos);

        var basis = new Basis(
            new Vector3(m[0], m[1], -m[2]),   // X axis (column 0)
            new Vector3(m[4], m[5], -m[6]),   // Y axis (column 1)
            new Vector3(-m[8], -m[9], m[10])); // Z axis (column 2)
        return new Transform3D(basis, new Vector3(m[12], m[13], -m[14]));
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
