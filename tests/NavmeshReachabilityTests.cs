using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

public class NavmeshReachabilityTests
{
    // A strip of quads laid along Z, each 1 m deep and `width` wide, sharing their vertices so the
    // triangles are genuinely edge-adjacent — the same indexing the baked meshes use.
    private static NavFlag Strip(int quads, float width = 2f, float y = 0f)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        for (int i = 0; i <= quads; i++)
        {
            vertices.Add(new Vector3(-width / 2f, y, i));
            vertices.Add(new Vector3(width / 2f, y, i));
        }
        for (int i = 0; i < quads; i++)
        {
            int a = i * 2, b = a + 1, c = a + 2, d = a + 3;
            triangles.AddRange(new[] { a, b, c });
            triangles.AddRange(new[] { b, d, c });
        }
        return new NavFlag
        {
            Vertices = vertices.ToArray(),
            Triangles = triangles.ToArray(),
            Center = Vector3.Zero,
            Size = Vector3.One * 100f,
        };
    }

    private static NavFlag Grid(int xQuads, int zQuads)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        int stride = zQuads + 1;
        for (int x = 0; x <= xQuads; x++)
            for (int z = 0; z <= zQuads; z++)
                vertices.Add(new Vector3(x, 0, z));
        for (int x = 0; x < xQuads; x++)
            for (int z = 0; z < zQuads; z++)
            {
                int a = x * stride + z, b = a + 1, c = a + stride, d = c + 1;
                triangles.AddRange(new[] { a, b, c, b, d, c });
            }
        return new NavFlag
        {
            Vertices = vertices.ToArray(),
            Triangles = triangles.ToArray(),
            Center = new Vector3(xQuads / 2f, 0, zQuads / 2f),
            Size = new Vector3(xQuads + 1, 10, zQuads + 1),
        };
    }

    // Ground that is flat except for a raised band over a range of Z — a sill, or a step.
    private static NavmeshReachability.SurfaceProbe Ledge(float fromZ, float toZ, float height)
    {
        return (Vector3 point, out float y) =>
        {
            y = point.Z >= fromZ && point.Z <= toZ ? height : 0f;
            return true;
        };
    }

    [Fact]
    public void FlatGround_DropsNothing()
    {
        NavFlag flag = Strip(6);
        HashSet<int> drop = NavmeshReachability.Unreachable(flag, 0.5f, (Vector3 _, out float y) =>
        {
            y = 0f;
            return true;
        });

        Assert.Empty(drop);
    }

    // The regression this exists for: PEI's House_01 sill is 1.00 m, the agent's step is 0.5, and the
    // baked mesh bridges it. Those faces have to go, or a zombie is routed into a wall it cannot climb.
    [Fact]
    public void LedgeTallerThanTheStep_IsDropped()
    {
        NavFlag flag = Strip(8);
        HashSet<int> drop = NavmeshReachability.Unreachable(flag, 0.5f, Ledge(3f, 4f, 1.0f));

        Assert.NotEmpty(drop);
        // Everything dropped must actually sit on the ledge.
        foreach (int t in drop)
        {
            float centreZ = ((flag.Vertices[flag.Triangles[t * 3]].Z
                + flag.Vertices[flag.Triangles[(t * 3) + 1]].Z
                + flag.Vertices[flag.Triangles[(t * 3) + 2]].Z) / 3f);
            Assert.InRange(centreZ, 2.5f, 4.5f);
        }
    }

    [Fact]
    public void AdjacentFacesUniformlyBridgingAWindowSill_AreStillDropped()
    {
        // A small opening can be covered by one navmesh quad (two faces). Every probe on both faces
        // then hits the same 1 m sill, so neighbour-only comparison sees 1 m versus 1 m and incorrectly
        // calls the bridge walkable. The authored faces themselves are at ground level: that discrepancy
        // is sufficient evidence that the CharacterController cannot follow them through the window.
        NavFlag flag = Strip(1, width: 2f, y: 0f);

        HashSet<int> drop = NavmeshReachability.Unreachable(flag, 0.5f,
            (Vector3 _, out float y) => { y = 1f; return true; });

        Assert.Equal(2, drop.Count);
    }

    [Fact]
    public void AuthoredElevatedFloorMatchingCollision_IsNotMistakenForASill()
    {
        NavFlag flag = Strip(2, width: 2f, y: 3f);

        HashSet<int> drop = NavmeshReachability.Unreachable(flag, 0.5f,
            (Vector3 _, out float y) => { y = 3f; return true; });

        Assert.Empty(drop);
    }

    [Fact]
    public void AContinuousAuthoredSlope_IsNotMistakenForSuccessiveSteps()
    {
        // Real baked faces are not all small or level. PEI contains broad triangles whose authored
        // surface rises gradually across several metres. Comparing the absolute highest ray hit on one
        // face to its neighbour folds that gradual rise into the body's step height and carves a false
        // hole, even when collision follows the authored plane exactly.
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        for (int row = 0; row <= 2; row++)
        {
            float z = row * 10f;
            float y = row;
            vertices.Add(new Vector3(-1f, y, z));
            vertices.Add(new Vector3(1f, y, z));
        }
        for (int row = 0; row < 2; row++)
        {
            int a = row * 2, b = a + 1, c = a + 2, d = a + 3;
            triangles.AddRange(new[] { a, b, c, b, d, c });
        }
        var flag = new NavFlag
        {
            Vertices = vertices.ToArray(),
            Triangles = triangles.ToArray(),
            Center = new Vector3(0f, 1f, 10f),
            Size = new Vector3(4f, 4f, 22f),
        };

        HashSet<int> drop = NavmeshReachability.Unreachable(flag, 0.5f,
            (Vector3 point, out float y) => { y = point.Y; return true; });

        Assert.Empty(drop);
    }

    [Fact]
    public void AbruptAuthoredTransitionMatchingCollision_IsStillDropped()
    {
        // A collision sampler that follows the authored surface exactly reports zero residual at every
        // point. That must not hide an over-height transition: the middle quad climbs one metre over
        // 20 cm, far steeper than the body can walk, while the broad floors on either side stay valid.
        float[] z = { 0f, 1f, 1.2f, 2.2f };
        float[] y = { 0f, 0f, 1f, 1f };
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        for (int row = 0; row < z.Length; row++)
        {
            vertices.Add(new Vector3(-1f, y[row], z[row]));
            vertices.Add(new Vector3(1f, y[row], z[row]));
        }
        for (int row = 0; row + 1 < z.Length; row++)
        {
            int a = row * 2, b = a + 1, c = a + 2, d = a + 3;
            triangles.AddRange(new[] { a, b, c, b, d, c });
        }
        var flag = new NavFlag
        {
            Vertices = vertices.ToArray(),
            Triangles = triangles.ToArray(),
            Center = new Vector3(0f, 0.5f, 1.1f),
            Size = new Vector3(4f, 4f, 4f),
        };

        HashSet<int> drop = NavmeshReachability.Unreachable(flag, 0.5f,
            (Vector3 point, out float surface) => { surface = point.Y; return true; });

        Assert.Equal(new HashSet<int> { 2, 3 }, drop);
    }

    [Fact]
    public void WallCuttingAFace_IsDroppedEvenWhenItsTopIsWithinOneAuthoredStep()
    {
        // The real Post_0 window has this geometry: Recast authored the face 0.66 m above the approach
        // floor, so the sill top is only 0.34 m above the FACE and a highest-only clearance calls it
        // climbable. The capsule still has to climb the full metre from the outside half of that same
        // face. Measuring the steep physical rise exposes that discontinuity without comparing broad
        // neighbours or mistaking an ordinary slope for a step.
        NavFlag flag = Strip(1, width: 2f, y: 0.66f);

        HashSet<int> drop = NavmeshReachability.Unreachable(flag, 0.5f,
            (Vector3 point, out float y) =>
            {
                y = point.X < 0f ? 0f : 1f;
                return true;
            });

        Assert.Equal(2, drop.Count);
    }

    [Fact]
    public void OneLocalObstacleDoesNotDeleteAWholeBroadFloorFace()
    {
        NavFlag flag = Strip(1, width: 10f);
        var centres = new Vector3[2];
        for (int triangle = 0; triangle < 2; triangle++)
            centres[triangle] = (flag.Vertices[flag.Triangles[triangle * 3]]
                + flag.Vertices[flag.Triangles[(triangle * 3) + 1]]
                + flag.Vertices[flag.Triangles[(triangle * 3) + 2]]) / 3f;

        HashSet<int> drop = NavmeshReachability.Unreachable(flag, 0.5f,
            (Vector3 point, out float y) =>
            {
                y = point.DistanceTo(centres[0]) < 0.01f
                    || point.DistanceTo(centres[1]) < 0.01f ? 1.2f : 0f;
                return true;
            });

        Assert.Empty(drop);
    }

    [Fact]
    public void AWholeBroadFaceAboveTheStepIsStillRemoved()
    {
        NavFlag flag = Strip(1, width: 10f);

        HashSet<int> drop = NavmeshReachability.Unreachable(flag, 0.5f,
            (Vector3 _, out float y) => { y = 1.2f; return true; });

        Assert.Equal(2, drop.Count);
    }

    [Fact]
    public void HalfOfTheMeasuredBroadFaceIsNotAWholeFaceObstruction()
    {
        Vector3 a = new(0f, 0f, 0f), b = new(10f, 0f, 0f), c = new(0f, 0f, 10f);
        Vector3[] points = { a, b, c, (a + b) * 0.5f, (b + c) * 0.5f, (c + a) * 0.5f };
        float[] surfaces = { 1.2f, 1.2f, 1.2f, 0f, 0f, 0f };

        float climb = NavmeshReachability.RequiredClimbForFace(a, b, c, 0.5f, 1.2f,
            points, surfaces);

        Assert.Equal(0f, climb);
    }

    [Fact]
    public void WindowFacesAreRemoved_AndPlannerUsesTheOpenDoorCorridor()
    {
        // Two rooms separated by a raised collision band. The baked ground mesh incorrectly spans the
        // band (the window); its bottom end is the real door. After reconciliation, A* must retain a
        // route, but every portal route has to descend into that opening rather than cross at the window.
        NavFlag flag = Grid(4, 3);
        HashSet<int> drop = NavmeshReachability.Unreachable(flag, 0.5f,
            (Vector3 point, out float y) =>
            {
                y = point.X >= 1.4f && point.X <= 2.6f && point.Z >= 1.2f ? 1f : 0f;
                return true;
            });
        BakedNavGraph graph = BakedNavGraph.Build(new[] { flag },
            new Dictionary<NavFlag, HashSet<int>> { [flag] = drop });
        var path = new List<Vector3>();

        Assert.NotEmpty(drop);
        Assert.True(graph.TryPath(new Vector3(0.1f, 0, 1.5f), new Vector3(3.9f, 0, 1.5f), path));
        Assert.Contains(path, point => point.Z <= 1.01f);
    }

    [Theory]
    [InlineData(0.1f)]  // a kerb
    [InlineData(0.3f)]  // a stair tread
    [InlineData(0.49f)] // just inside the step
    public void LedgeWithinTheStep_IsKept(float height)
    {
        NavFlag flag = Strip(8);
        Assert.Empty(NavmeshReachability.Unreachable(flag, 0.5f, Ledge(3f, 4f, height)));
    }

    // The reason sampling is not just the centre: a face can span an opening and the solid beside it,
    // and its centre can land in the opening. Judging by the centre alone keeps the face, and the
    // funnel then cuts the shortest line straight across its solid half.
    [Fact]
    public void ObstacleMissedByTheCentre_IsStillCaught()
    {
        NavFlag flag = Strip(6, width: 4f);
        // A solid band along one side only, narrow enough that face centres (x = 0) never touch it.
        NavmeshReachability.SurfaceProbe sideWall = (Vector3 point, out float y) =>
        {
            y = point.X > 1.2f && point.Z >= 2f && point.Z <= 4f ? 1.0f : 0f;
            return true;
        };

        Assert.NotEmpty(NavmeshReachability.Unreachable(flag, 0.5f, sideWall));
    }

    [Fact]
    public void SamplePoints_CoverTheFaceNotJustItsCentre()
    {
        var a = new Vector3(0, 0, 0);
        var b = new Vector3(3, 0, 0);
        var c = new Vector3(0, 0, 3);
        var points = new List<Vector3>(NavmeshReachability.SamplePoints(a, b, c));

        Assert.Equal(7, points.Count);
        Vector3 centre = (a + b + c) / 3f;
        Assert.Equal(centre, points[0]);
        // Every sample stays on the face (barycentric coordinates all non-negative).
        foreach (Vector3 p in points)
        {
            float w0 = ((b.Z - c.Z) * (p.X - c.X) + (c.X - b.X) * (p.Z - c.Z))
                / ((b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z));
            float w1 = ((c.Z - a.Z) * (p.X - c.X) + (a.X - c.X) * (p.Z - c.Z))
                / ((b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z));
            Assert.InRange(w0, -0.001f, 1.001f);
            Assert.InRange(w1, -0.001f, 1.001f);
            Assert.InRange(1f - w0 - w1, -0.001f, 1.001f);
        }
        // ...and they are spread out, not all sitting on top of each other.
        Assert.True(points[1].DistanceTo(points[2]) > 0.5f);
    }

    // A probe that finds nothing must not be read as "ground at zero" — that would delete every face
    // over a hole, and holes in the collision world are exactly where the baked data is all we have.
    [Fact]
    public void FacesWithNoSurfaceUnderThem_AreLeftAlone()
    {
        NavFlag flag = Strip(6, y: 5f);
        HashSet<int> drop = NavmeshReachability.Unreachable(flag, 0.5f, (Vector3 point, out float y) =>
        {
            y = 0f;
            return point.Z < 2f; // nothing sampled beyond z=2
        });

        foreach (int t in drop)
        {
            float centreZ = (flag.Vertices[flag.Triangles[t * 3]].Z
                + flag.Vertices[flag.Triangles[(t * 3) + 1]].Z
                + flag.Vertices[flag.Triangles[(t * 3) + 2]].Z) / 3f;
            Assert.True(centreZ < 2.5f, $"face at z={centreZ} was judged without a surface under it");
        }
    }

    [Fact]
    public void AnIsolatedFace_IsKept()
    {
        // One triangle, no neighbours: nothing to compare against, so nothing can be concluded.
        var flag = new NavFlag
        {
            Vertices = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 0, 1) },
            Triangles = new[] { 0, 1, 2 },
            Center = Vector3.Zero,
            Size = Vector3.One * 10f,
        };

        Assert.Empty(NavmeshReachability.Unreachable(flag, 0.5f, (Vector3 _, out float y) =>
        {
            y = 99f; // absurdly high, but there is no neighbour to be unreachable FROM
            return true;
        }));
    }

    [Fact]
    public void EmptyFlag_IsHandled()
    {
        var flag = new NavFlag { Center = Vector3.Zero, Size = Vector3.One };
        Assert.Empty(NavmeshReachability.Unreachable(flag, 0.5f, (Vector3 _, out float y) =>
        {
            y = 0f;
            return true;
        }));
    }

    // Dropping is directional: the high side is unreachable from the low side, but the low side stays
    // walkable. Deleting both would carve a hole in ground that is perfectly fine to stand on.
    [Fact]
    public void OnlyTheHighSideOfADropIsRemoved()
    {
        NavFlag flag = Strip(8);
        HashSet<int> drop = NavmeshReachability.Unreachable(flag, 0.5f, Ledge(4f, 8f, 2f));

        int count = flag.Triangles.Length / 3;
        Assert.NotEmpty(drop);
        Assert.True(drop.Count < count, "every face was dropped; the low ground must survive");
    }
}
