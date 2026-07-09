using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Zombies;

public class ZombieNavmeshTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("navmesh-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    // Builds a Navigation_<N>.dat in UnturnedNavmesh_ASPFP.Serialize's exact format: version,
    // forcedBounds center+size, tile counts, then per tile ushort-counted tris and Int3 verts.
    private string WriteNavFile(int index, Vector3 center, Vector3 size,
        params (ushort[] Tris, (int X, int Y, int Z)[] Verts)[] tiles)
    {
        string path = Path.Combine(_dir, $"Navigation_{index}.dat");
        using var w = new BinaryWriter(File.Create(path));
        w.Write((byte)1);
        w.Write(center.X);
        w.Write(center.Y);
        w.Write(center.Z);
        w.Write(size.X);
        w.Write(size.Y);
        w.Write(size.Z);
        w.Write((byte)tiles.Length); // tileXCount
        w.Write((byte)1);            // tileZCount
        foreach ((ushort[] tris, (int X, int Y, int Z)[] verts) in tiles)
        {
            w.Write((ushort)tris.Length);
            foreach (ushort t in tris)
                w.Write(t);
            w.Write((ushort)verts.Length);
            foreach ((int x, int y, int z) in verts)
            {
                w.Write(x);
                w.Write(y);
                w.Write(z);
            }
        }
        return path;
    }

    [Fact]
    public void Read_ConvertsToGodotSpaceAndWeldsTileBorders()
    {
        // Two tiles, one triangle each, sharing the edge (1000,0,0)-(1000,0,1000) in Unity mm.
        // Triangles are authored with the REAL data's winding (cross-Y positive in raw Unity
        // space — verified across the actual PEI navmeshes).
        string path = WriteNavFile(0, new Vector3(10, 5, 20), new Vector3(40, 60, 50),
            (new ushort[] { 0, 1, 2 },
                new[] { (0, 0, 0), (1000, 0, 1000), (1000, 0, 0) }),
            (new ushort[] { 0, 1, 2 },
                new[] { (1000, 0, 0), (1000, 0, 1000), (2000, 0, 0) }));

        NavFlag? flag = LevelNavmesh.Read(path);
        Assert.NotNull(flag);
        Assert.Equal(new Vector3(10, 5, -20), flag!.Center); // Unity -> Godot Z mirror
        Assert.Equal(new Vector3(40, 60, 50), flag.Size);

        // 6 authored vertices weld into 4 (the two on the shared border merge exactly).
        Assert.Equal(4, flag.Vertices.Length);
        Assert.Equal(6, flag.Triangles.Length); // 2 triangles
        Assert.Contains(new Vector3(1, 0, 0), flag.Vertices);   // mm -> metres
        Assert.Contains(new Vector3(1, 0, -1), flag.Vertices);  // Z mirrored

        // The Z mirror flips winding; the parser reverses it back so faces keep pointing up.
        for (int i = 0; i + 2 < flag.Triangles.Length; i += 3)
        {
            Vector3 a = flag.Vertices[flag.Triangles[i]];
            Vector3 b = flag.Vertices[flag.Triangles[i + 1]];
            Vector3 c = flag.Vertices[flag.Triangles[i + 2]];
            Assert.True((b - a).Cross(c - a).Y > 0f, "triangle winding faces down");
        }

        // Both triangles reference the welded border vertices: shared indices, connected polygons.
        int[] tri0 = flag.Triangles.Take(3).ToArray();
        int[] tri1 = flag.Triangles.Skip(3).Take(3).ToArray();
        Assert.Equal(2, tri0.Intersect(tri1).Count());
    }

    [Fact]
    public void Read_VersionZero_ReturnsNull()
    {
        string path = Path.Combine(_dir, "Navigation_0.dat");
        File.WriteAllBytes(path, new byte[] { 0 });
        Assert.Null(LevelNavmesh.Read(path));
    }

    [Fact]
    public void Load_ToleratesIndexGapsLikeTheOriginal()
    {
        // Files 0 and 3 exist (1, 2 were deleted flags); the scan must find both and stop after
        // five consecutive misses.
        WriteNavFile(0, new Vector3(0, 0, 0), new Vector3(10, 10, 10),
            (new ushort[] { 0, 1, 2 }, new[] { (0, 0, 0), (1000, 0, 0), (0, 0, 1000) }));
        WriteNavFile(3, new Vector3(50, 0, 0), new Vector3(10, 10, 10),
            (new ushort[] { 0, 1, 2 }, new[] { (0, 0, 0), (1000, 0, 0), (0, 0, 1000) }));
        File.WriteAllBytes(Path.Combine(_dir, "Navigation_4.dat"), new byte[] { 0 }); // empty flag

        List<NavFlag> flags = LevelNavmesh.Load(_dir);
        Assert.Equal(2, flags.Count);
        Assert.Equal(0f, flags[0].Center.X);
        Assert.Equal(50f, flags[1].Center.X);
    }

    [Fact]
    public void Load_MissingDirectory_ReturnsEmpty()
    {
        Assert.Empty(LevelNavmesh.Load(Path.Combine(_dir, "nope")));
    }

    [Fact]
    public void CheckNavigation_TestsTheNonExpandedBoxes()
    {
        var flags = new List<NavFlag>
        {
            new() { Center = new Vector3(0, 140, 0), Size = new Vector3(100, 200, 100) },
            new() { Center = new Vector3(500, 140, 0), Size = new Vector3(50, 200, 50) },
        };
        Assert.True(LevelNavmesh.CheckNavigation(flags, new Vector3(49, 0, -49)));
        Assert.True(LevelNavmesh.CheckNavigation(flags, new Vector3(510, 999, 10))); // Y ignored
        Assert.False(LevelNavmesh.CheckNavigation(flags, new Vector3(60, 0, 0)));    // outside both
        Assert.False(LevelNavmesh.CheckNavigation(flags, new Vector3(0, 0, 60)));
    }

    private static NavFlag Flag(Vector3 center, Vector3 size, Vector3[] verts, int[] tris) =>
        new() { Center = center, Size = size, Vertices = verts, Triangles = tris };

    [Fact]
    public void SnapXZ_PrefersTheClosestLevel_NeverTheBasement()
    {
        // Two stacked floors under the same XZ: a street at y=34 and a basement at y=30.5.
        var flags = new List<NavFlag>
        {
            Flag(new Vector3(0, 32, 0), new Vector3(40, 20, 40), new[]
            {
                new Vector3(-10, 34, -10), new Vector3(10, 34, -10), new Vector3(0, 34, 10),   // street
                new Vector3(-10, 30.5f, -10), new Vector3(10, 30.5f, -10), new Vector3(0, 30.5f, 10), // basement
            }, new[] { 0, 1, 2, 3, 4, 5 }),
        };

        Assert.True(LevelNavmesh.SnapXZ(flags, new Vector3(0, 34.2f, 0), out Vector3 snapped));
        Assert.Equal(34f, snapped.Y, 2);   // the player's own floor, 3.5 m above the basement
        Assert.Equal(0f, snapped.X, 3);    // XZ preserved when contained
        Assert.True(LevelNavmesh.SnapXZ(flags, new Vector3(0, 30.4f, 0), out snapped));
        Assert.Equal(30.5f, snapped.Y, 2); // someone actually IN the basement snaps down there
    }

    [Fact]
    public void SnapXZ_OffMesh_TakesTheSameLevelEdge_WithVerticalPenalty()
    {
        // The point sits off the mesh; a same-level edge 1.5 m away must beat a basement edge
        // 1.2 m away in XZ but 3 m below.
        var flags = new List<NavFlag>
        {
            Flag(new Vector3(0, 32, 0), new Vector3(40, 20, 40), new[]
            {
                new Vector3(1.5f, 34, -5), new Vector3(1.5f, 34, 5), new Vector3(6, 34, 0),      // same level
                new Vector3(-1.2f, 31, -5), new Vector3(-1.2f, 31, 5), new Vector3(-6, 31, 0),   // below
                new Vector3(8, 34, 8), new Vector3(8, 34, 8), new Vector3(9, 34, 9),             // degenerate: skipped
            }, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 }),
        };

        Assert.True(LevelNavmesh.SnapXZ(flags, new Vector3(0, 34f, 0), out Vector3 snapped));
        Assert.Equal(1.5f, snapped.X, 2);
        Assert.Equal(34f, snapped.Y, 2);
    }

    [Fact]
    public void SnapXZ_FarFromEveryFlag_ReturnsFalse()
    {
        var flags = new List<NavFlag>
        {
            Flag(new Vector3(0, 32, 0), new Vector3(10, 20, 10),
                new[] { new Vector3(-1, 34, -1), new Vector3(1, 34, -1), new Vector3(0, 34, 1) },
                new[] { 0, 1, 2 }),
        };
        Assert.False(LevelNavmesh.SnapXZ(flags, new Vector3(500, 34, 500), out _));
    }

    [Fact]
    public void RealPei_TheReportedOffMeshSpot_SnapsToTheStreetNotTheBasement()
    {
        string pei = "/home/jlucaso/.local/share/Steam/steamapps/common/Unturned/Maps/PEI";
        if (!Directory.Exists(pei))
            return;
        List<NavFlag> flags = LevelNavmesh.Load(Path.Combine(pei, "Environment"));
        // The reported zigzag spot: a player standing off-mesh at street level (y~34) with a
        // basement mesh ~3 m below. The stable snap must pick the street.
        Assert.True(LevelNavmesh.SnapXZ(flags, new Vector3(333.28f, 34.0f, 99.94f), out Vector3 snapped));
        Assert.True(snapped.Y > 33f, $"snapped into the basement: {snapped}");
        Assert.True(new Vector2(snapped.X - 333.28f, snapped.Z - 99.94f).Length() < 6f,
            $"snapped too far: {snapped}");
    }

    // Real PEI data (self-skips without the game): 19 pre-baked navmeshes; every flag's box must
    // match its Bounds.dat entry minus the BOUNDS_SIZE (64 m) expansion.
    [Fact]
    public void RealPei_NavmeshesParseAndMatchTheBounds()
    {
        string pei = "/home/jlucaso/.local/share/Steam/steamapps/common/Unturned/Maps/PEI";
        if (!Directory.Exists(pei))
            return;
        string env = Path.Combine(pei, "Environment");

        List<NavFlag> flags = LevelNavmesh.Load(env);
        Assert.Equal(19, flags.Count);
        Assert.Equal(42642, flags.Sum(f => f.Triangles.Length / 3));

        List<NavBound> bounds = LevelNavigationData.Load(env);
        Assert.Equal(bounds.Count, flags.Count);
        for (int i = 0; i < flags.Count; i++)
        {
            Assert.Equal(bounds[i].Center.X, flags[i].Center.X, 0.5f);
            Assert.Equal(bounds[i].Center.Z, flags[i].Center.Z, 0.5f); // both Godot-mirrored
            Assert.Equal(bounds[i].Size.X - 64f, flags[i].Size.X, 0.5f);
            Assert.Equal(bounds[i].Size.Z - 64f, flags[i].Size.Z, 0.5f);
        }

        // Every triangle index is in range and every vertex sits inside its flag's box (with the
        // baker's border margin) — a corrupted parse would explode either invariant.
        foreach (NavFlag flag in flags)
        {
            Assert.True(flag.Vertices.Length > 0);
            Assert.All(flag.Triangles, i => Assert.InRange(i, 0, flag.Vertices.Length - 1));
            // The tile grid rounds the box up to whole 12.8 m tiles, so vertices may overhang
            // the forcedBounds by up to one tile.
            foreach (Vector3 v in flag.Vertices)
            {
                Assert.True(MathF.Abs(v.X - flag.Center.X) <= (flag.Size.X * 0.5f) + 13f,
                    $"vertex {v} escapes flag at {flag.Center}");
                Assert.True(MathF.Abs(v.Z - flag.Center.Z) <= (flag.Size.Z * 0.5f) + 13f,
                    $"vertex {v} escapes flag at {flag.Center}");
            }
        }

        // Spawn-town sanity: the player spawn area sits on the navmesh of flag 2 (Charlottetown).
        Assert.True(LevelNavmesh.CheckNavigation(flags, new Vector3(300, 34, 84)));
    }
}
