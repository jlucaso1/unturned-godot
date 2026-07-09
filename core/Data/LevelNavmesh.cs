using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace UnturnedGodot.Data;

// One nav flag's PRE-BAKED navmesh, deserialized from Environment/Navigation_<N>.dat — the exact
// triangles Unturned's editor baked (A* Pathfinding Project RecastGraph tiles; format decompiled
// from UnturnedNavmesh_ASPFP.Serialize). Everything is converted to Godot space here: coordinates
// mirror Z and scale from Int3 millimetres to metres, and the per-tile meshes are welded into one
// indexed mesh so polygons connect across tile borders.
public sealed class NavFlag
{
    // The NON-expanded navmesh bounds (forcedBounds): LevelNavigation.checkNavigation tests these,
    // while Bounds.dat carries the same boxes expanded by BOUNDS_SIZE (64 m).
    public Vector3 Center;
    public Vector3 Size;

    public Vector3[] Vertices = Array.Empty<Vector3>();
    public int[] Triangles = Array.Empty<int>(); // 3 indices per triangle

    // LevelNavigation.checkNavigation's per-flag test: the point sits inside the non-expanded box.
    public bool ContainsXZ(Vector3 point) =>
        Mathf.Abs(point.X - Center.X) <= Size.X * 0.5f &&
        Mathf.Abs(point.Z - Center.Z) <= Size.Z * 0.5f;
}

public static class LevelNavmesh
{
    // LevelNavigation.load scans Navigation_<i>.dat by index and stops after five consecutive
    // missing files (maps may have gaps where flags were deleted in the editor).
    public static List<NavFlag> Load(string environmentDir)
    {
        var flags = new List<NavFlag>();
        int consecutiveNotFound = 0;
        for (int index = 0; consecutiveNotFound < 5; index++)
        {
            string path = Path.Combine(environmentDir, $"Navigation_{index}.dat");
            if (!File.Exists(path))
            {
                consecutiveNotFound++;
                continue;
            }
            consecutiveNotFound = 0;
            NavFlag? flag = Read(path);
            if (flag != null)
                flags.Add(flag);
        }
        return flags;
    }

    // UnturnedNavmesh_ASPFP.Deserialize, byte for byte: version, forcedBounds center+size,
    // tileXCount/tileZCount, then per tile (z-major) the ushort-counted triangle indices and
    // Int3 world-space vertices (millimetres).
    public static NavFlag? Read(string path)
    {
        using var river = new River(path);
        byte version = river.ReadByte();
        if (version == 0)
            return null;

        Vector3 center = river.ReadSingleVector3();
        Vector3 size = river.ReadSingleVector3();
        center.Z = -center.Z; // Unity -> Godot mirror (sizes are mirror-invariant)
        byte tileXCount = river.ReadByte();
        byte tileZCount = river.ReadByte();

        // Weld identical vertices across tiles by their exact Int3 key: RecastGraph tiles duplicate
        // the vertices along shared borders, and Godot only connects polygons over shared indices.
        var vertexIndex = new Dictionary<(int X, int Y, int Z), int>();
        var vertices = new List<Vector3>();
        var triangles = new List<int>();

        int tileCount = tileXCount * tileZCount;
        for (int t = 0; t < tileCount; t++)
        {
            ushort indexCount = river.ReadUInt16();
            var tris = new ushort[indexCount];
            for (int i = 0; i < indexCount; i++)
                tris[i] = river.ReadUInt16();

            ushort vertCount = river.ReadUInt16();
            var map = new int[vertCount];
            for (int v = 0; v < vertCount; v++)
            {
                int x = river.ReadInt32();
                int y = river.ReadInt32();
                int z = river.ReadInt32();
                (int, int, int) key = (x, y, -z); // mirror in the integer domain: exact welds
                if (!vertexIndex.TryGetValue(key, out int welded))
                {
                    welded = vertices.Count;
                    vertexIndex[key] = welded;
                    vertices.Add(new Vector3(x / 1000f, y / 1000f, -z / 1000f));
                }
                map[v] = welded;
            }

            // The Z-mirror flips the winding; reverse each triangle so the surface keeps facing up.
            for (int i = 0; i + 2 < indexCount; i += 3)
            {
                triangles.Add(map[tris[i]]);
                triangles.Add(map[tris[i + 2]]);
                triangles.Add(map[tris[i + 1]]);
            }
        }

        return new NavFlag
        {
            Center = center,
            Size = size,
            Vertices = vertices.ToArray(),
            Triangles = triangles.ToArray(),
        };
    }

    // LevelNavigation.checkNavigation: is the point inside ANY flag's non-expanded navmesh box?
    public static bool CheckNavigation(IReadOnlyList<NavFlag> flags, Vector3 point)
    {
        for (int i = 0; i < flags.Count; i++)
            if (flags[i].ContainsXZ(point))
                return true;
        return false;
    }
}
