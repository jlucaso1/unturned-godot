using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

// `LevelNavmesh.SnapXZ` used to be a flat scan over every triangle of every candidate flag. It is now a
// walk over uniform XZ buckets, and the whole claim of that change is that it is a speed change and
// nothing else: the point it hands back has to be the same point, bit for bit, or a zombie's route
// starts or ends somewhere it did not before.
//
// The characterization tests in tests/Zombies/ZombieNavmeshTests.cs pin the SEMANTICS — closest level,
// slope interpolation, the vertical penalty on an off-mesh edge, false when far from every flag — and
// they pass unchanged. That is necessary and not sufficient: they are a handful of points chosen to
// describe the rules, and the rules are not where an index goes wrong. An index goes wrong on the
// points nobody thought to name — a tie between two faces, a face in the next bucket that reaches into
// this one, a query outside the grid entirely — and it goes wrong by a few centimetres, which no
// assertion about "the street, not the basement" would catch.
//
// So this compares against the implementation it replaces, kept here verbatim, over the map's own
// navmesh and over meshes built to be awkward. Exact equality, not approximate: the two are supposed to
// compute the same floats in the same order, and a tolerance would hide the tie-break bugs that are the
// entire risk of the change.
public class NavSnapIndexTests
{
    // ---- the flat scan, exactly as it stood before the index ------------------------------------

    private static bool ReferenceSnapXZ(IReadOnlyList<NavFlag> flags, Vector3 point, out Vector3 snapped)
    {
        snapped = point;
        float bestContainedDy = float.MaxValue;
        float bestEdgeScore = float.MaxValue;
        Vector3 bestEdge = point;
        bool contained = false;

        foreach (NavFlag flag in flags)
        {
            if (Mathf.Abs(point.X - flag.Center.X) > (flag.Size.X * 0.5f) + 16f
                || Mathf.Abs(point.Z - flag.Center.Z) > (flag.Size.Z * 0.5f) + 16f)
                continue;

            Vector3[] v = flag.Vertices;
            int[] t = flag.Triangles;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
                float area2 = ((b.X - a.X) * (c.Z - a.Z)) - ((c.X - a.X) * (b.Z - a.Z));
                if (MathF.Abs(area2) < 1e-6f)
                    continue;
                float d1 = ((point.X - b.X) * (a.Z - b.Z)) - ((a.X - b.X) * (point.Z - b.Z));
                float d2 = ((point.X - c.X) * (b.Z - c.Z)) - ((b.X - c.X) * (point.Z - c.Z));
                float d3 = ((point.X - a.X) * (c.Z - a.Z)) - ((c.X - a.X) * (point.Z - a.Z));
                bool neg = d1 < 0f || d2 < 0f || d3 < 0f;
                bool pos = d1 > 0f || d2 > 0f || d3 > 0f;
                if (!(neg && pos))
                {
                    float w1 = (((b.Z - c.Z) * (point.X - c.X)) + ((c.X - b.X) * (point.Z - c.Z))) / area2;
                    float w2 = (((c.Z - a.Z) * (point.X - c.X)) + ((a.X - c.X) * (point.Z - c.Z))) / area2;
                    float y = (w1 * a.Y) + (w2 * b.Y) + ((1f - w1 - w2) * c.Y);
                    float dy = Mathf.Abs(y - point.Y);
                    if (dy < bestContainedDy)
                    {
                        bestContainedDy = dy;
                        snapped = new Vector3(point.X, y, point.Z);
                        contained = true;
                    }
                }
                else if (!contained)
                {
                    ReferenceClosestOnEdge(a, b, point, ref bestEdgeScore, ref bestEdge);
                    ReferenceClosestOnEdge(b, c, point, ref bestEdgeScore, ref bestEdge);
                    ReferenceClosestOnEdge(c, a, point, ref bestEdgeScore, ref bestEdge);
                }
            }
        }

        if (contained)
            return true;
        if (bestEdgeScore < float.MaxValue)
        {
            snapped = bestEdge;
            return true;
        }
        return false;
    }

    private static void ReferenceClosestOnEdge(Vector3 a, Vector3 b, Vector3 point,
        ref float bestScore, ref Vector3 best)
    {
        float dx = b.X - a.X, dz = b.Z - a.Z;
        float lengthSquared = (dx * dx) + (dz * dz);
        float t = Mathf.Clamp((((point.X - a.X) * dx) + ((point.Z - a.Z) * dz)) / lengthSquared, 0f, 1f);
        Vector3 candidate = a.Lerp(b, t);
        float ddx = point.X - candidate.X, ddz = point.Z - candidate.Z;
        float dy = point.Y - candidate.Y;
        float score = (ddx * ddx) + (ddz * ddz) + (0.25f * dy * dy);
        if (score < bestScore)
        {
            bestScore = score;
            best = candidate;
        }
    }

    // ---- the comparison ---------------------------------------------------------------------------

    private static void AssertSame(IReadOnlyList<NavFlag> flags, Vector3 point)
    {
        bool expected = ReferenceSnapXZ(flags, point, out Vector3 want);
        bool actual = LevelNavmesh.SnapXZ(flags, point, out Vector3 got);
        Assert.True(expected == actual,
            $"at {point}: the flat scan said {expected}, the index said {actual}");
        Assert.True(want == got, $"at {point}: the flat scan said {want}, the index said {got}");
    }

    private static NavFlag Flag(Vector3 center, Vector3 size, Vector3[] verts, int[] tris) =>
        new() { Center = center, Size = size, Vertices = verts, Triangles = tris };

    // A square of `side` x `side` cells at `height`, offset in XZ, split into two triangles each.
    private static NavFlag Grid(int side, float step, Vector3 origin, float height, Vector3 boxCenter,
        Vector3 boxSize)
    {
        var vertices = new Vector3[(side + 1) * (side + 1)];
        for (int x = 0; x <= side; x++)
            for (int z = 0; z <= side; z++)
                vertices[(x * (side + 1)) + z] =
                    new Vector3(origin.X + (x * step), height, origin.Z + (z * step));
        var triangles = new int[side * side * 6];
        int at = 0;
        for (int x = 0; x < side; x++)
            for (int z = 0; z < side; z++)
            {
                int a = (x * (side + 1)) + z, b = a + 1, c = a + side + 1, d = c + 1;
                triangles[at++] = a;
                triangles[at++] = b;
                triangles[at++] = c;
                triangles[at++] = b;
                triangles[at++] = d;
                triangles[at++] = c;
            }
        return Flag(boxCenter, boxSize, vertices, triangles);
    }

    // A flat floor, swept at a fine enough stride to land inside faces, on their shared edges, on
    // their vertices, and off the mesh on every side.
    [Fact]
    public void OverAFlatFloor_TheIndexAndTheFlatScanAgreeEverywhere()
    {
        var flags = new List<NavFlag>
        {
            Grid(12, 2f, new Vector3(0, 0, 0), 4f, new Vector3(12, 0, 12), new Vector3(60, 40, 60)),
        };

        for (float x = -8f; x <= 32f; x += 0.5f)
            for (float z = -8f; z <= 32f; z += 0.5f)
                AssertSame(flags, new Vector3(x, 4f, z));
    }

    // Two floors over the same XZ, which is where the |dy| comparison decides the answer, swept at
    // heights between, below and above both of them.
    [Fact]
    public void OverStackedFloors_EveryHeightPicksTheSameOne()
    {
        var flags = new List<NavFlag>
        {
            Grid(8, 2f, new Vector3(0, 0, 0), 4f, new Vector3(8, 0, 8), new Vector3(60, 40, 60)),
            Grid(8, 2f, new Vector3(0, 0, 0), 0.5f, new Vector3(8, 0, 8), new Vector3(60, 40, 60)),
        };

        for (float y = -3f; y <= 9f; y += 0.25f)
            for (float x = -4f; x <= 20f; x += 1.5f)
                for (float z = -4f; z <= 20f; z += 1.5f)
                    AssertSame(flags, new Vector3(x, y, z));
    }

    // Two flags whose boxes overlap: the flat scan resolved a tie between them by flag order, and the
    // index has to say so explicitly because it no longer meets them in that order by accident.
    [Fact]
    public void AcrossOverlappingFlags_TheEarlierFlagStillWinsATie()
    {
        // Identical geometry in both flags, so every candidate ties exactly with its twin.
        var flags = new List<NavFlag>
        {
            Grid(6, 2f, new Vector3(0, 0, 0), 3f, new Vector3(6, 0, 6), new Vector3(60, 40, 60)),
            Grid(6, 2f, new Vector3(0, 0, 0), 3f, new Vector3(6, 0, 6), new Vector3(60, 40, 60)),
        };

        for (float x = -6f; x <= 18f; x += 0.75f)
            for (float z = -6f; z <= 18f; z += 0.75f)
                AssertSame(flags, new Vector3(x, 3f, z));
    }

    // The mesh is a long way from the point but still inside the flag's box + 16 m margin, so the walk
    // has to expand rings until it reaches geometry rather than give up at the first empty one.
    [Fact]
    public void WithTheMeshFarFromThePoint_TheWalkStillReachesIt()
    {
        var flags = new List<NavFlag>
        {
            Grid(10, 3f, new Vector3(140, 0, 140), 7f, new Vector3(100, 0, 100),
                new Vector3(400, 40, 400)),
        };

        for (float x = -80f; x <= 280f; x += 11f)
            for (float z = -80f; z <= 280f; z += 11f)
                AssertSame(flags, new Vector3(x, 7f, z));
    }

    // Zero-area faces "contain" every point under the sign rule, so they are indexed like any other and
    // rejected on area at query time. Indexing them is what keeps that rejection reachable.
    [Fact]
    public void DegenerateFacesAreRejectedNotFollowed()
    {
        var flags = new List<NavFlag>
        {
            Flag(new Vector3(0, 0, 0), new Vector3(80, 40, 80), new[]
            {
                new Vector3(-4, 2, -4), new Vector3(4, 2, -4), new Vector3(0, 2, 4),
                new Vector3(1, 9, 1), new Vector3(1, 9, 1), new Vector3(1, 9, 1),     // a point
                new Vector3(-6, 9, -6), new Vector3(6, 9, 6), new Vector3(-3, 9, -3), // collinear
            }, new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 }),
        };

        for (float x = -12f; x <= 12f; x += 0.4f)
            for (float z = -12f; z <= 12f; z += 0.4f)
                AssertSame(flags, new Vector3(x, 2f, z));
    }

    // A mesh laid out symmetrically about the query points, so the closest edge on the left and the
    // closest edge on the right score identically. The flat scan took whichever it met first.
    [Fact]
    public void WhenTwoEdgesTieExactly_TheSameOneIsChosen()
    {
        // Two triangles mirrored about x = 0, and a gap between them the point sits in.
        var flags = new List<NavFlag>
        {
            Flag(new Vector3(0, 0, 0), new Vector3(80, 40, 80), new[]
            {
                new Vector3(-5, 1, -5), new Vector3(-5, 1, 5), new Vector3(-9, 1, 0),
                new Vector3(5, 1, 5), new Vector3(5, 1, -5), new Vector3(9, 1, 0),
            }, new[] { 0, 1, 2, 3, 4, 5 }),
        };

        for (float z = -6f; z <= 6f; z += 0.25f)
            AssertSame(flags, new Vector3(0f, 1f, z));
    }

    // A flag with no geometry at all, and a point nowhere near anything: both have to answer false the
    // same way, and neither may index its way into an exception.
    [Fact]
    public void EmptyFlagsAndDistantPointsAgree()
    {
        var flags = new List<NavFlag>
        {
            Flag(new Vector3(0, 0, 0), new Vector3(40, 20, 40), Array.Empty<Vector3>(),
                Array.Empty<int>()),
            Grid(4, 2f, new Vector3(0, 0, 0), 1f, new Vector3(4, 0, 4), new Vector3(40, 20, 40)),
        };

        AssertSame(flags, new Vector3(500, 1, 500));
        AssertSame(flags, new Vector3(-500, 1, 0));
        AssertSame(flags, new Vector3(4, 1, 4));
        Assert.False(LevelNavmesh.SnapXZ(flags, new Vector3(500, 1, 500), out _));
    }

    // Replacing a flag's geometry has to replace its index with it. Nothing in the game does this, but
    // the fields are assignable and the survey, the editor dock and the repro harness all build flags
    // by hand — an index keyed on the flag rather than on its arrays would answer from the old mesh.
    [Fact]
    public void ReplacingTheGeometryReindexesIt()
    {
        NavFlag flag = Grid(4, 2f, new Vector3(0, 0, 0), 1f, new Vector3(4, 0, 4),
            new Vector3(60, 20, 60));
        var flags = new List<NavFlag> { flag };

        Assert.True(LevelNavmesh.SnapXZ(flags, new Vector3(4, 1, 4), out Vector3 before));
        Assert.Equal(1f, before.Y, 4);

        NavFlag replacement = Grid(4, 2f, new Vector3(0, 0, 0), 12f, new Vector3(4, 0, 4),
            new Vector3(60, 20, 60));
        flag.Vertices = replacement.Vertices;
        flag.Triangles = replacement.Triangles;

        Assert.True(LevelNavmesh.SnapXZ(flags, new Vector3(4, 12, 4), out Vector3 after));
        Assert.Equal(12f, after.Y, 4);
        AssertSame(flags, new Vector3(4, 12, 4));
        AssertSame(flags, new Vector3(-3, 12, -3));
    }

    // The map's own navmesh, which is the geometry the change was made for: 42,642 triangles across 19
    // flags on PEI, with T-junctions, overlapping storeys and the tile seams a synthetic grid has none
    // of. Swept over each flag's box and past its edges, so both branches and the flag reject are hit.
    [RealDataFact(Map = "PEI")]
    public void OverTheRealPeiNavmesh_TheIndexAndTheFlatScanAgree()
    {
        string pei = GameData.Map("PEI")!;
        List<NavFlag> flags = LevelNavmesh.Load(Path.Combine(pei, "Environment"));
        Assert.NotEmpty(flags);

        int sampled = 0;
        foreach (NavFlag flag in flags)
        {
            // Past the box on both axes, so the +16 m margin and the reject beyond it are both covered.
            float halfX = (flag.Size.X * 0.5f) + 24f;
            float halfZ = (flag.Size.Z * 0.5f) + 24f;
            for (float dx = -halfX; dx <= halfX; dx += halfX / 6f)
                for (float dz = -halfZ; dz <= halfZ; dz += halfZ / 6f)
                {
                    var at = new Vector3(flag.Center.X + dx, flag.Center.Y, flag.Center.Z + dz);
                    // Several heights per column: which storey wins is decided on |dy|, and one
                    // height per column would only ever exercise one side of that comparison.
                    foreach (float y in new[] { -4f, 0f, 6f, 20f, 40f })
                    {
                        AssertSame(flags, at with { Y = at.Y + y });
                        sampled++;
                    }
                }
        }

        Assert.True(sampled > 5000, $"only {sampled} points sampled");
    }

    // Every vertex of the real navmesh, which is where the containment sign test is exactly on the
    // boundary — three faces meeting at a point all "contain" it, and which of them the answer comes
    // from is decided purely by the tie-break the index had to reproduce.
    [RealDataFact(Map = "PEI")]
    public void OnEveryRealNavmeshVertex_TheSameFaceWins()
    {
        string pei = GameData.Map("PEI")!;
        List<NavFlag> flags = LevelNavmesh.Load(Path.Combine(pei, "Environment"));

        foreach (NavFlag flag in flags)
            for (int i = 0; i < flag.Vertices.Length; i += 17)
                AssertSame(flags, flag.Vertices[i]);
    }
}
