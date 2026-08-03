using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Zombies;

// Two symptoms, reported from play: in houses and tight spaces zombies sometimes stop pressed against a
// wall, and over open ground they take a route that wanders when nothing is in the way.
//
// Both are properties of the route the funnel produces, so both are measurable here without an engine.
// Every threshold below was chosen after measuring the broken behaviour, and each comment records what
// that measurement was — the numbers are the specification, not decoration.
public class PathQualityTests
{
    private const float DoorMinZ = 7f;
    private const float DoorMaxZ = 8f;
    private const float WallMinX = 10f;
    private const float WallMaxX = 11f;

    // A perfectly flat, unobstructed field: quadsPerSide squared quads, two triangles each.
    private static NavFlag FlatField(int quadsPerSide)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        int side = quadsPerSide + 1;
        for (int x = 0; x < side; x++)
            for (int z = 0; z < side; z++)
                vertices.Add(new Vector3(x, 0f, z));
        for (int x = 0; x < quadsPerSide; x++)
            for (int z = 0; z < quadsPerSide; z++)
            {
                int a = (x * side) + z, b = a + 1, c = a + side, d = c + 1;
                triangles.AddRange(new[] { a, b, c, b, d, c });
            }
        return new NavFlag
        {
            Center = new Vector3(quadsPerSide / 2f, 0, quadsPerSide / 2f),
            Size = new Vector3(quadsPerSide + 2f, 100f, quadsPerSide + 2f),
            Vertices = vertices.ToArray(),
            Triangles = triangles.ToArray(),
        };
    }

    // A wall across x in [10, 11] with a doorway ONE quad wide at z in [7, 8] — a house door.
    private static (NavFlag Flag, BakedNavGraph Graph) Doorway(int quads = 20)
    {
        NavFlag flag = FlatField(quads);
        var blocked = new HashSet<int>();
        for (int z = 0; z < quads; z++)
        {
            if (z == (int)DoorMinZ)
                continue;
            int quad = (10 * quads) + z;
            blocked.Add(quad * 2);
            blocked.Add((quad * 2) + 1);
        }
        return (flag, BakedNavGraph.Build(new[] { flag },
            new Dictionary<NavFlag, HashSet<int>> { [flag] = blocked }));
    }

    // Sweep and slide against the wall slab, modelling what the host actually installs: a CastMotion
    // that advances to first contact and projects the REMAINING motion onto that surface, iterated a few
    // times. It never depenetrates — the body stops at the surface — and it never refuses outright, it
    // slides. Both of those matter: a refusing resolver overstates the bug and a depenetrating one
    // hides it, and I got a different answer from each before writing this one.
    private static ZombieMoveResolver WallWithDoorway() => (from, to, radius) =>
    {
        (float MinX, float MaxX, float MinZ, float MaxZ)[] boxes =
        {
            (WallMinX, WallMaxX, -100f, DoorMinZ),
            (WallMinX, WallMaxX, DoorMaxZ, 100f),
        };

        static bool Overlaps(Vector3 p, float radius,
            (float MinX, float MaxX, float MinZ, float MaxZ) box)
        {
            float cx = Math.Clamp(p.X, box.MinX, box.MaxX);
            float cz = Math.Clamp(p.Z, box.MinZ, box.MaxZ);
            float dx = p.X - cx, dz = p.Z - cz;
            return (dx * dx) + (dz * dz) < radius * radius;
        }

        bool Blocked(Vector3 p)
        {
            foreach (var box in boxes)
                if (Overlaps(p, radius, box))
                    return true;
            return false;
        }

        // Fine substeps: each one either lands free, or is taken on whichever single axis stays free —
        // which is the projection of the remaining motion onto the contact plane for an axis-aligned
        // wall. Anything that would enter the surface is simply not taken.
        const int Substeps = 16;
        Vector3 at = from;
        Vector3 step = (to - from) / Substeps;
        for (int i = 0; i < Substeps; i++)
        {
            var full = new Vector3(at.X + step.X, to.Y, at.Z + step.Z);
            if (!Blocked(full))
            {
                at = full;
                continue;
            }
            var alongZ = new Vector3(at.X, to.Y, at.Z + step.Z);
            if (!Blocked(alongZ))
            {
                at = alongZ;
                continue;
            }
            var alongX = new Vector3(at.X + step.X, to.Y, at.Z);
            if (!Blocked(alongX))
                at = alongX;
        }
        return at;
    };

    private static float Length(List<Vector3> path)
    {
        float total = 0f;
        for (int i = 1; i < path.Count; i++)
            total += path[i - 1].DistanceTo(path[i]);
        return total;
    }

    // SYMPTOM: "over open ground they trace a path that goes around when there is no obstacle."
    //
    // Measured on flat, empty ground: runs along either axis were already exactly 1.000, so whatever is
    // wrong is specific to diagonals. This pins that open ground is never made WORSE — the failure mode
    // an agent-radius inset introduces if it is applied to interior portals as well as walls, which was
    // measured at 1.254 and 43 waypoints before being narrowed to borders only.
    [Theory]
    [InlineData(2f, 16f, 30f, 16f, 1.001f, 4)]   // straight along X
    [InlineData(16f, 2f, 16f, 30f, 1.001f, 4)]   // straight along Z
    [InlineData(2f, 2f, 30f, 5f, 1.04f, 6)]      // a shallow angle
    [InlineData(2f, 2f, 30f, 30f, 1.18f, 14)]    // the diagonal
    public void OpenGround_IsNotMadeWorse(float fromX, float fromZ, float toX, float toZ,
        float maxRatio, int maxWaypoints)
    {
        BakedNavGraph graph = BakedNavGraph.Build(new[] { FlatField(32) });
        var from = new Vector3(fromX, 0, fromZ);
        var to = new Vector3(toX, 0, toZ);
        var path = new List<Vector3>();

        Assert.True(graph.TryPath(from, to, path));

        float ratio = Length(path) / from.DistanceTo(to);
        Assert.True(ratio <= maxRatio, $"walked {ratio:0.000}x the direct distance (limit {maxRatio})");
        Assert.True(path.Count <= maxWaypoints, $"{path.Count} waypoints (limit {maxWaypoints})");
    }

    // SYMPTOM: "in houses and tight spaces they sometimes stop pressed against the walls."
    //
    // The geometric half. A portal endpoint is a mesh VERTEX, and at a doorway that vertex is the jamb,
    // so string-pulling to it aims a body with width at a point ON the wall. Measured before the fix,
    // with an angled approach: a waypoint landed exactly on a jamb — clearance 0.000 against a 0.40 m
    // capsule.
    [Fact]
    public void ADoorwayRoute_KeepsTheBodysWidthOffTheJambs()
    {
        (NavFlag _, BakedNavGraph graph) = Doorway();
        var path = new List<Vector3>();
        Assert.True(graph.TryPath(new Vector3(4, 0, 2), new Vector3(16, 0, 15), path));

        var jambs = new[]
        {
            new Vector3(WallMinX, 0, DoorMinZ), new Vector3(WallMaxX, 0, DoorMinZ),
            new Vector3(WallMinX, 0, DoorMaxZ), new Vector3(WallMaxX, 0, DoorMaxZ),
        };

        foreach (Vector3 point in path)
            foreach (Vector3 jamb in jambs)
            {
                float clearance = new Vector2(point.X - jamb.X, point.Z - jamb.Z).Length();
                Assert.True(clearance > 0.3f,
                    $"waypoint ({point.X:0.00}, {point.Z:0.00}) sits {clearance:0.000} m from a jamb");
            }
    }

    // And the behavioural half, which is the symptom itself: the same doorway, a real wall in the
    // MoveResolver seam, and a zombie hunting a player on the far side.
    //
    // Before the fix it ended at (10.17, 7.59) — inside the wall slab — and never got through in 20 s.
    // A 1 m opening leaves a 0.4 m capsule only a 0.2 m band for its centre, and the route aimed it
    // 0.4 m outside that band, at the jamb itself.
    [Fact]
    public void AZombieHuntsThroughAHouseDoorway_InsteadOfStallingOnTheJamb()
    {
        (NavFlag flag, BakedNavGraph graph) = Doorway();
        var bounds = new List<NavBound>
        {
            new NavBound { Center = new Vector3(10, 50, 10), Size = new Vector3(60, 200, 60) },
        };
        var system = new ZombieSystem(new[] { new ZombieTable { Name = "C", Health = 100, Damage = 10 } },
            bounds, (float x, float z, out float y) => { y = 0f; return true; }, new[] { flag });
        // Spawnpoints carry UNITY coordinates, so Z is pre-flipped to land on (5, 6) in Godot space.
        system.Spawn(new[] { new ZombieSpawnpointData(0, new Vector3(5, 0, -6)) }, new Random(1));
        ZombieInstance zombie = Assert.Single(system.Zombies);
        zombie.Speciality = EZombieSpeciality.Normal;
        zombie.Position = new Vector3(5, 0, 6);

        system.PathQuery = (Vector3 a, Vector3 b, List<Vector3> p) => graph.TryPath(a, b, p);
        system.PathReady = () => true;
        system.MoveResolver = WallWithDoorway();

        var player = new[]
        {
            new ZombiePlayerView(1, new Vector3(13, 0, 9), UnturnedGodot.Player.EPlayerStance.Stand, false),
        };

        bool through = false;
        for (int tick = 0; tick < 250 && !through; tick++) // 20 s at the server tick
        {
            system.Tick(player, 0.08f);
            through = zombie.Position.X > WallMaxX + 0.5f;
        }

        Assert.True(through, "the zombie never cleared the doorway; it ended at "
            + $"({zombie.Position.X:0.00}, {zombie.Position.Z:0.00})");
    }

    // The inset must not invent a route where a body does not fit, nor refuse one where it does. A
    // portal narrower than the body degrades to its midpoint rather than inverting into a backwards
    // portal, and whether the capsule physically passes stays the collision resolver's call.
    [Fact]
    public void ANarrowGapStillRoutes_AimedDownItsMiddle()
    {
        (NavFlag _, BakedNavGraph graph) = Doorway();
        var path = new List<Vector3>();
        Assert.True(graph.TryPath(new Vector3(4, 0, 7.5f), new Vector3(16, 0, 7.5f), path));

        foreach (Vector3 point in path)
            if (point.X >= WallMinX && point.X <= WallMaxX)
                Assert.InRange(point.Z, DoorMinZ, DoorMaxZ);
    }
}
