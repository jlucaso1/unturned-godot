using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class NavmeshSurveyTests
{
    // A flat strip of `quads` squares along +X, offset so several can be built side by side without
    // sharing a vertex — two strips at different Z are two separate walkable surfaces.
    private static NavFlag Strip(int quads, float z = 0f)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        for (int x = 0; x <= quads; x++)
        {
            vertices.Add(new Vector3(x, 0, z));
            vertices.Add(new Vector3(x, 0, z + 1));
        }
        for (int x = 0; x < quads; x++)
        {
            int a = x * 2, b = a + 1, c = a + 2, d = a + 3;
            triangles.AddRange(new[] { a, b, c, b, d, c });
        }
        return new NavFlag
        {
            Center = new Vector3(quads / 2f, 0, z + 0.5f),
            Size = new Vector3(quads + 1f, 10, 2f),
            Vertices = vertices.ToArray(),
            Triangles = triangles.ToArray(),
        };
    }

    // Two strips in one flag, 10 m apart in Z with no shared vertex: one file, two surfaces.
    private static NavFlag TwoStrips(int nearQuads, int farQuads)
    {
        NavFlag near = Strip(nearQuads);
        NavFlag far = Strip(farQuads, z: 10f);
        var vertices = new List<Vector3>(near.Vertices);
        var triangles = new List<int>(near.Triangles);
        int offset = near.Vertices.Length;
        vertices.AddRange(far.Vertices);
        foreach (int index in far.Triangles)
            triangles.Add(index + offset);
        return new NavFlag
        {
            Center = new Vector3(0, 0, 5),
            Size = new Vector3(40, 10, 40),
            Vertices = vertices.ToArray(),
            Triangles = triangles.ToArray(),
        };
    }

    private static NavFlagSurvey SurveyOf(NavFlag flag,
        IReadOnlyDictionary<NavFlag, HashSet<int>>? unreachable = null) =>
        BakedNavGraph.Build(new[] { flag }, unreachable).Survey()[0];

    [Fact]
    public void AConnectedStrip_IsOneIsland()
    {
        NavFlagSurvey survey = SurveyOf(Strip(4));

        Assert.Equal(8, survey.TriangleCount);
        Assert.Equal(0, survey.DroppedTriangles);
        NavIsland island = Assert.Single(survey.Islands);
        Assert.Equal(8, island.TriangleCount);
        Assert.All(survey.IslandOfTriangle, index => Assert.Equal(0, index));
        Assert.Equal(1.0, survey.LargestIslandShare);
        // Four quads of unit squares, two triangles each.
        Assert.Equal(4.0, island.Area, 5);
        Assert.Equal(new Vector3(0, 0, 0), island.Min);
        Assert.Equal(new Vector3(4, 0, 1), island.Max);
    }

    [Fact]
    public void SeparateSurfaces_AreSeparateIslands_LargestFirst()
    {
        NavFlagSurvey survey = SurveyOf(TwoStrips(nearQuads: 2, farQuads: 5));

        Assert.Equal(2, survey.Islands.Count);
        Assert.Equal(10, survey.Islands[0].TriangleCount); // the 5-quad strip, ranked first
        Assert.Equal(4, survey.Islands[1].TriangleCount);
        // The near strip was built first, so ranking really did reorder rather than keep discovery order.
        Assert.Equal(1, survey.IslandOfTriangle[0]);
        Assert.Equal(0, survey.IslandOfTriangle[^1]);
        Assert.Equal(10 / 14.0, survey.LargestIslandShare, 6);
    }

    // The whole reason this hangs off BakedNavGraph. Two halves of one floor meeting along a line they
    // disagree about the ends of share no vertex pair, so read off the file they are two islands with a
    // wall between them — a hole that is not there. The graph stitches them; the survey has to agree.
    [Fact]
    public void AFloorSplitByATJunction_SurveysAsOneIslandWithNoRimAcrossTheSeam()
    {
        var flag = new NavFlag
        {
            Center = new Vector3(2, 0, 1),
            Size = new Vector3(6, 10, 4),
            Vertices = new[]
            {
                new Vector3(0, 0, 0), new Vector3(0, 0, 2), new Vector3(2, 0, 0), new Vector3(2, 0, 2),
                new Vector3(2, 0, 1), new Vector3(4, 0, 0), new Vector3(4, 0, 1), new Vector3(4, 0, 2),
            },
            Triangles = new[] { 0, 1, 2, 1, 3, 2, 2, 4, 5, 4, 6, 5, 4, 3, 6, 3, 7, 6 },
        };

        NavFlagSurvey survey = SurveyOf(flag);

        Assert.Single(survey.Islands);
        Assert.Equal(6, survey.Islands[0].TriangleCount);
        // And the seam is not drawn as a wall: every rim edge sits on the floor's outer boundary, so
        // none of them runs along x = 2 between z = 0 and z = 2.
        Assert.DoesNotContain(survey.Rim, edge => edge.A.X == 2f && edge.B.X == 2f);
    }

    // The same seam, but the far side only reaches halfway along it: the join is real for the stretch
    // that has floor on both sides and a wall for the rest. All-or-nothing on the whole edge would
    // either hide a metre of missing floor or invent a metre of wall, and both send you to the wrong
    // place; what is drawn is the part that is actually a wall.
    [Fact]
    public void AHalfStitchedSeam_DrawsOnlyTheHalfWithNoFloorAcrossIt()
    {
        var flag = new NavFlag
        {
            Center = new Vector3(2, 0, 1),
            Size = new Vector3(6, 10, 4),
            Vertices = new[]
            {
                new Vector3(0, 0, 0), new Vector3(0, 0, 2), new Vector3(2, 0, 0), new Vector3(2, 0, 2),
                new Vector3(2, 0, 1), new Vector3(4, 0, 0), new Vector3(4, 0, 1),
            },
            // Left half spans x = 2 from z = 0 to z = 2; the right half only covers z = 0 to z = 1.
            Triangles = new[] { 0, 1, 2, 1, 3, 2, 2, 4, 5, 4, 6, 5 },
        };

        NavFlagSurvey survey = SurveyOf(flag);

        Assert.Single(survey.Islands); // still one surface: the stitched half joins them
        NavRimEdge seam = Assert.Single(survey.Rim, edge => edge.A.X == 2f && edge.B.X == 2f);
        Assert.Equal(1f, MathF.Min(seam.A.Z, seam.B.Z), 4);
        Assert.Equal(2f, MathF.Max(seam.A.Z, seam.B.Z), 4);
    }

    // A face standing on its edge covers no ground, so from above it has no outline: one of its edges
    // is a point in plan and the other two run along the face itself. The overlay is a plan view, and
    // two arbitrary lines for a face with no footprint are worse than none. It is still a face the
    // router holds, which is why the island survives.
    [Fact]
    public void AFaceStandingOnItsEdge_DrawsNoOutline()
    {
        NavFlagSurvey survey = SurveyOf(new NavFlag
        {
            Center = new Vector3(0.5f, 0, 0.5f),
            Size = new Vector3(4, 4, 4),
            Vertices = new[] { new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 0, 1) },
            Triangles = new[] { 0, 1, 2 },
        });

        Assert.Empty(survey.Rim);
        Assert.Equal(1, Assert.Single(survey.Islands).TriangleCount);
    }

    // Two faces on the SAME side of an edge are stacked or duplicated, not joined across it. Counting
    // one as floor for the other would erase a real boundary, and the route would then be allowed to
    // run along the very lip of the mesh with the body hanging off it.
    [Fact]
    public void AFaceDuplicatedOnTheSameSide_DoesNotEraseTheBoundary()
    {
        NavFlagSurvey survey = SurveyOf(new NavFlag
        {
            Center = new Vector3(0.5f, 0, 0.5f),
            Size = new Vector3(4, 4, 4),
            Vertices = new[]
            {
                new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 0),
            },
            Triangles = new[] { 0, 1, 2, 0, 1, 2 }, // the same face twice
        });

        // Three edges, each still a wall despite the second copy naming every one of them.
        Assert.Equal(3, survey.Rim.Count);
    }

    // A zero-area face lies ALONG an edge rather than to one side of it, so the sign that decides
    // "is there floor across this" is zero and rejects every neighbour. Left able to claim the edge,
    // such a face would report a wall down the middle of open floor — and, because the edge is only
    // answered once, would stop the two real faces that share it from saying otherwise. Which one wins
    // came down to face order, so this is pinned with the sliver FIRST.
    [Fact]
    public void ASliverAlongAnEdge_DoesNotClaimItFromTheFacesWithASide()
    {
        NavFlagSurvey survey = SurveyOf(new NavFlag
        {
            Center = new Vector3(0, 0, 0.5f),
            Size = new Vector3(6, 10, 6),
            Vertices = new[]
            {
                new Vector3(0, 0, 0),    // 0 ─┐ the shared edge
                new Vector3(0, 0, 1),    // 1 ─┘
                new Vector3(0, 0, 2),    // 2  collinear with it: the sliver's third corner
                new Vector3(-1, 0, 0.5f),// 3  left of the edge
                new Vector3(1, 0, 0.5f), // 4  right of the edge
            },
            Triangles = new[] { 0, 1, 2, 0, 1, 3, 0, 1, 4 },
        });

        // The two real faces sit either side of x = 0, so there is floor across it and no wall there.
        Assert.DoesNotContain(survey.Rim, edge => edge.A.X == 0f && edge.B.X == 0f
            && MathF.Min(edge.A.Z, edge.B.Z) >= 0f && MathF.Max(edge.A.Z, edge.B.Z) <= 1f);
        // The outer boundary of the two real faces is still drawn: two free edges each.
        Assert.Equal(4, survey.Rim.Count);
    }

    // LevelNavmesh welds vertices by their exact quantised position, so two corners of one recast
    // triangle landing on the same Int3 come back as the SAME index — a face like {0, 0, 2}. Its edge
    // (0, 2) has no third corner off it, and asking which side that corner is on read Vertices[-1].
    [Fact]
    public void AFaceWithARepeatedCorner_DoesNotReachPastTheVertexArray()
    {
        NavFlagSurvey survey = SurveyOf(new NavFlag
        {
            Center = new Vector3(0.5f, 0, 0.5f),
            Size = new Vector3(4, 4, 4),
            Vertices = new[]
            {
                new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 0),
            },
            // The welded face first, so it is the one that reaches each of its edges first.
            Triangles = new[] { 0, 0, 2, 0, 1, 2 },
        });

        // The real face still describes itself: three free edges, none of them from the welded one.
        Assert.Equal(3, survey.Rim.Count);
        Assert.Equal(2, Assert.Single(survey.Islands).TriangleCount);
    }

    // The same surface, written down differently. A cyclic rotation of a triangle's indices preserves
    // both the geometry and the winding, so every rotation has to survey identically — and it did not:
    // a stitched portal carries the OVERLAP of two edges, so one of its ends can be a vertex the
    // neighbour does not own, and asking that neighbour which corner is "opposite" the pair then
    // returned its first corner in winding order instead of nothing. On a seam that corner is the far
    // end of the join itself, lying ON the line and scoring side zero — which read as "not across",
    // discarded the stitch, and drew a red line over open floor. Rotations differed by two segments.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ATJunctionSurveysTheSame_HoweverItsIndicesAreRotated(int rotation)
    {
        int[][] faces =
        {
            new[] { 0, 1, 2 }, new[] { 1, 3, 2 },  // left half, spanning x = 2 in one edge
            new[] { 2, 4, 5 }, new[] { 4, 6, 5 },  // right half, lower
            new[] { 4, 3, 6 }, new[] { 3, 7, 6 },  // right half, upper
        };
        var triangles = faces
            .SelectMany(face => Enumerable.Range(0, 3).Select(i => face[(i + rotation) % 3]))
            .ToArray();

        NavFlagSurvey survey = SurveyOf(new NavFlag
        {
            Center = new Vector3(2, 0, 1),
            Size = new Vector3(6, 10, 4),
            Vertices = new[]
            {
                new Vector3(0, 0, 0), new Vector3(0, 0, 2), new Vector3(2, 0, 0), new Vector3(2, 0, 2),
                new Vector3(2, 0, 1), new Vector3(4, 0, 0), new Vector3(4, 0, 1), new Vector3(4, 0, 2),
            },
            Triangles = triangles,
        });

        Assert.Equal(6, Assert.Single(survey.Islands).TriangleCount);
        // The outline of a 4x2 rectangle at this tessellation, and nothing along the seam at x = 2.
        Assert.Equal(7, survey.Rim.Count);
        Assert.DoesNotContain(survey.Rim, edge => edge.A.X == 2f && edge.B.X == 2f);
    }

    [Fact]
    public void RimIsTheOuterBoundary_CountedOnce()
    {
        NavFlagSurvey survey = SurveyOf(Strip(4));

        // A 4x1 strip's boundary: 4 unit edges along each long side, plus one at each end. The shared
        // diagonals and the interior verticals have floor on both sides and are not rim.
        Assert.Equal(10, survey.Rim.Count);
        Assert.All(survey.Rim, edge =>
            Assert.True(edge.A.Z == edge.B.Z || edge.A.X == edge.B.X,
                $"{edge.A} -> {edge.B} is a diagonal, so it is interior"));
        Assert.Equal(survey.Rim.Count, survey.Rim.Select(e => (e.A, e.B)).Distinct().Count());
    }

    // The headline case the overlay exists for: a floor with a square punched out of the middle. The
    // surface stays one island — you can walk around the hole — so island colouring alone would show
    // nothing, and the rim is the only thing that draws it.
    [Fact]
    public void AHolePunchedInAFloor_IsDrawnAsRimWithoutSplittingTheIsland()
    {
        const int Side = 4;
        var vertices = new List<Vector3>();
        for (int x = 0; x < Side; x++)
            for (int z = 0; z < Side; z++)
                vertices.Add(new Vector3(x, 0, z));
        var triangles = new List<int>();
        for (int x = 0; x < Side - 1; x++)
            for (int z = 0; z < Side - 1; z++)
            {
                if (x == 1 && z == 1)
                    continue; // the hole
                int at = (x * Side) + z;
                int right = at + Side;
                triangles.AddRange(new[] { at, at + 1, right, at + 1, right + 1, right });
            }

        NavFlagSurvey survey = SurveyOf(new NavFlag
        {
            Center = new Vector3(1.5f, 0, 1.5f),
            Size = new Vector3(6, 10, 6),
            Vertices = vertices.ToArray(),
            Triangles = triangles.ToArray(),
        });

        Assert.Equal(16, Assert.Single(survey.Islands).TriangleCount);
        // Twelve unit edges around the outside, four around the hole.
        Assert.Equal(16, survey.Rim.Count);
        Assert.Equal(4, survey.Rim.Count(edge =>
            edge.A.X is >= 1f and <= 2f && edge.B.X is >= 1f and <= 2f &&
            edge.A.Z is >= 1f and <= 2f && edge.B.Z is >= 1f and <= 2f));
    }

    [Fact]
    public void AnchorSitsOnTheSurface_NotInTheNotch()
    {
        // An L: two 6x1 arms meeting at the origin corner, cut out of a shared 7x7 vertex grid so the
        // whole thing really is one surface. The centroid of the shape falls at about (1.9, 1.9) —
        // in the notch, off the mesh, which is exactly where a marker must not be placed.
        const int Side = 7;
        var vertices = new List<Vector3>();
        for (int x = 0; x < Side; x++)
            for (int z = 0; z < Side; z++)
                vertices.Add(new Vector3(x, 0, z));
        var triangles = new List<int>();
        for (int x = 0; x < Side - 1; x++)
            for (int z = 0; z < Side - 1; z++)
            {
                if (x != 0 && z != 0)
                    continue;
                int at = (x * Side) + z;
                int right = at + Side;
                triangles.AddRange(new[] { at, at + 1, right, at + 1, right + 1, right });
            }

        NavFlagSurvey survey = SurveyOf(new NavFlag
        {
            Center = new Vector3(3, 0, 3),
            Size = new Vector3(14, 10, 14),
            Vertices = vertices.ToArray(),
            Triangles = triangles.ToArray(),
        });

        Vector3 anchor = Assert.Single(survey.Islands).Anchor;
        // Every face centre of an L drawn one unit wide has X < 6 and Z < 6, and one of the two arms
        // has a coordinate under 1 — the notch's own centre (about 1.6, 1.6) satisfies neither.
        Assert.True(anchor.X < 1f || anchor.Z < 1f,
            $"anchor {anchor} is in the notch rather than on one of the arms");
        Assert.Contains(vertices, v => v.DistanceTo(anchor) < 1f);
    }

    [Fact]
    public void DroppedFacesLeaveTheIslands_AndCanSplitThem()
    {
        NavFlag strip = Strip(4);
        // Both faces of the third quad: the strip is cut into a 2-quad piece and a 1-quad piece.
        var unreachable = new Dictionary<NavFlag, HashSet<int>> { [strip] = new() { 4, 5 } };

        NavFlagSurvey survey = SurveyOf(strip, unreachable);

        Assert.Equal(2, survey.DroppedTriangles);
        Assert.Equal(6, survey.WalkableTriangles);
        Assert.Equal(-1, survey.IslandOfTriangle[4]);
        Assert.Equal(-1, survey.IslandOfTriangle[5]);
        Assert.Equal(new[] { 4, 2 }, survey.Islands.Select(i => i.TriangleCount));
        Assert.Equal(4 / 6.0, survey.LargestIslandShare, 6);
        // And the cut itself is now wall: the far side of the surviving quad has no floor across it.
        Assert.Contains(survey.Rim, edge => edge.A.X == 3f && edge.B.X == 3f);
    }

    [Fact]
    public void PocketsAreTheIslandsUnderTheThreshold()
    {
        NavFlagSurvey survey = SurveyOf(TwoStrips(nearQuads: 1, farQuads: 8));

        (int Index, NavIsland Island) pocket = Assert.Single(survey.Pockets(maxTriangles: 4));
        Assert.Equal(1, pocket.Index);
        Assert.Equal(2, pocket.Island.TriangleCount);
        Assert.Equal(2, survey.Pockets(maxTriangles: 16).Count());
        Assert.Empty(survey.Pockets(maxTriangles: 1));
    }

    // The shipped map, because the synthetic cases above each pin one rule and none of them can say
    // whether the whole thing describes real Recast output. The figures are characterization: they are
    // not targets, they are what PEI IS, and a change to any of them means the graph's own topology
    // moved — which is the thing the overlay is drawn from, so it should have to be looked at.
    [RealDataFact(Map = "PEI")]
    public void PeiSurveysAsTheRouterSeesIt()
    {
        var flags = LevelNavmesh.Load(Path.Combine(GameData.Map("PEI")!, "Environment"));
        IReadOnlyList<NavFlagSurvey> survey = BakedNavGraph.Build(flags).Survey();

        Assert.Equal(19, survey.Count);
        Assert.Equal(42642, survey.Sum(s => s.TriangleCount));
        Assert.Equal(415, survey.Sum(s => s.Islands.Count));
        Assert.Equal(359, survey.Sum(s => s.Pockets(maxTriangles: 64).Count()));

        // Read off the file, 37 396 of PEI's edges have no other face naming the same vertex pair. The
        // stitched seams are not walls, and the overlay must not draw them as such: the rim it reports
        // is measurably shorter than the naive count, which is the whole reason this hangs off the
        // graph rather than off NavFlag.
        //
        // 33 947 until the side test stopped being asked of stitched portals. Those portals carry the
        // overlap of two edges, so an end of one can be a vertex the neighbour does not own, and the
        // "which corner is opposite" question then answered with the neighbour's first corner in
        // winding order — on a seam, its far endpoint, lying ON the join and scoring side zero. Zero
        // read as "not across", the stitch was discarded, and a red line went across open floor. It
        // was live on this map: 1 204 of the segments drawn here were that, phantom walls over ground
        // the router walks straight over. Fewer is the right direction — a rim segment that is not a
        // wall is the one failure this overlay exists to avoid.
        int naiveBorders = flags.Sum(NaiveBorderEdges);
        Assert.Equal(37396, naiveBorders);
        Assert.Equal(32743, survey.Sum(s => s.Rim.Count));

        // Nothing dropped: reconciliation against collision is a runtime pass, and this is the map as
        // baked.
        Assert.All(survey, s => Assert.Equal(0, s.DroppedTriangles));
        // And every island's anchor is somewhere the eye can be sent: inside that island's own extent.
        // Deliberately not tested against the FLAG's box — recast tiles overhang the authored bounds by
        // metres, which is why snapping allows a 16 m margin against them.
        Assert.All(survey, s => Assert.All(s.Islands, island =>
            Assert.True(island.Anchor.X >= island.Min.X && island.Anchor.X <= island.Max.X
                && island.Anchor.Z >= island.Min.Z && island.Anchor.Z <= island.Max.Z,
                $"anchor {island.Anchor} is outside its own island {island.Min}..{island.Max}")));
    }

    // Edges no other face names the same vertex pair for — the classification the overlay would have
    // used if it read the triangles instead of the graph.
    private static int NaiveBorderEdges(NavFlag flag)
    {
        var counts = new Dictionary<(int Low, int High), int>();
        for (int at = 0; at + 2 < flag.Triangles.Length; at += 3)
            for (int edge = 0; edge < 3; edge++)
            {
                int a = flag.Triangles[at + edge], b = flag.Triangles[at + ((edge + 1) % 3)];
                var key = (Math.Min(a, b), Math.Max(a, b));
                counts[key] = counts.TryGetValue(key, out int seen) ? seen + 1 : 1;
            }
        return counts.Values.Count(shared => shared == 1);
    }

    [Fact]
    public void AnEmptyFlagSurveysCleanly()
    {
        NavFlagSurvey survey = SurveyOf(new NavFlag
        {
            Center = Vector3.Zero,
            Size = new Vector3(4, 4, 4),
            Vertices = Array.Empty<Vector3>(),
            Triangles = Array.Empty<int>(),
        });

        Assert.Equal(0, survey.TriangleCount);
        Assert.Empty(survey.Islands);
        Assert.Empty(survey.Rim);
        Assert.Equal(0.0, survey.LargestIslandShare);
    }
}
