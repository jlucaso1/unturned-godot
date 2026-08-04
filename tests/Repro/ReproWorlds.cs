using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Net;
using UnturnedGodot.Repro;
using UnturnedGodot.Zombies;

namespace UnturnedGodot.Tests.Repro;

// A small world the repro tests share: flat ground, one nav flag over it, and whatever obstacles a
// test wants to stand in the way. Everything is built the way the game builds it — a navmesh for the
// routes, triangles for the collision — so a scenario running against this exercises the same code
// paths a dump from a real session does.
public static class ReproWorlds
{
    public const float Side = 40f;

    public static NavBound Bound() => new()
    {
        Center = new Vector3(Side / 2f, 0f, Side / 2f),
        Size = new Vector3(Side * 4f, 100f, Side * 4f),
        MaxZombies = 64,
        SpawnZombies = true,
    };

    // One quad per metre over the whole field, which is what the game's own baked navmesh looks like
    // over open ground.
    public static NavFlag FlatField(float cell = 1f)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        var side = (int)(Side / cell) + 1;
        for (int x = 0; x < side; x++)
            for (int z = 0; z < side; z++)
                vertices.Add(new Vector3(x * cell, 0f, z * cell));
        for (int x = 0; x < side - 1; x++)
            for (int z = 0; z < side - 1; z++)
            {
                int a = (x * side) + z, b = a + 1, c = a + side, d = c + 1;
                triangles.AddRange(new[] { a, b, c, b, d, c });
            }
        return new NavFlag
        {
            Center = new Vector3(Side / 2f, 0f, Side / 2f),
            Size = new Vector3(Side + 4f, 100f, Side + 4f),
            Vertices = vertices.ToArray(),
            Triangles = triangles.ToArray(),
        };
    }

    public static bool FlatGround(float x, float z, out float y)
    {
        y = 0f;
        return true;
    }

    // Collision triangles: the ground itself plus an upright box wherever a test wants one. The layer
    // is the solid world plus the vision blocker bit, which is what a LARGE object collider carries.
    public sealed class Geometry
    {
        private readonly List<float> _vertices = new();
        private readonly List<int> _triangles = new();
        private readonly List<int> _layers = new();
        private readonly List<int> _owners = new();
        private readonly List<string> _names = new();

        public Geometry Ground()
        {
            const float far = 200f;
            Triangle("ground", CollisionLayers.World,
                new Vector3(-far, 0f, -far), new Vector3(-far, 0f, far), new Vector3(far, 0f, far));
            Triangle("ground", CollisionLayers.World,
                new Vector3(-far, 0f, -far), new Vector3(far, 0f, far), new Vector3(far, 0f, -far));
            return this;
        }

        // An axis-aligned box standing on the ground: a pole, a wall segment, a crate.
        public Geometry Box(string name, Vector3 center, Vector3 halfExtents,
            uint layer = CollisionLayers.World | CollisionLayers.VisionBlocker)
        {
            var corners = new Vector3[8];
            for (int i = 0; i < 8; i++)
                corners[i] = center + new Vector3(
                    (i & 1) == 0 ? -halfExtents.X : halfExtents.X,
                    (i & 2) == 0 ? -halfExtents.Y : halfExtents.Y,
                    (i & 4) == 0 ? -halfExtents.Z : halfExtents.Z);
            Quad(name, layer, corners[0], corners[2], corners[3], corners[1]);
            Quad(name, layer, corners[4], corners[5], corners[7], corners[6]);
            Quad(name, layer, corners[0], corners[1], corners[5], corners[4]);
            Quad(name, layer, corners[2], corners[6], corners[7], corners[3]);
            Quad(name, layer, corners[0], corners[4], corners[6], corners[2]);
            Quad(name, layer, corners[1], corners[3], corners[7], corners[5]);
            return this;
        }

        private void Quad(string name, uint layer, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            Triangle(name, layer, a, b, c);
            Triangle(name, layer, a, c, d);
        }

        public void Triangle(string name, uint layer, Vector3 a, Vector3 b, Vector3 c)
        {
            int owner = _names.IndexOf(name);
            if (owner < 0)
            {
                owner = _names.Count;
                _names.Add(name);
            }
            foreach (Vector3 point in new[] { a, b, c })
            {
                _triangles.Add(_vertices.Count / 3);
                _vertices.Add(point.X);
                _vertices.Add(point.Y);
                _vertices.Add(point.Z);
            }
            _layers.Add((int)layer);
            _owners.Add(owner);
        }

        public ReproGeometryData Build(Vector3 center, float radius = 64f) => new()
        {
            Center = ReproVector.From(center),
            Radius = radius,
            Vertices = _vertices.ToArray(),
            Triangles = _triangles.ToArray(),
            Layers = _layers.ToArray(),
            Owners = _owners.ToArray(),
            OwnerNames = _names,
        };
    }

    // A live session over that world, wired exactly like the Godot host wires one: navmesh routes,
    // capsule collision, ground snap and vision all answered by the same code a replay uses, so a test
    // comparing the two is comparing the harness rather than two different physics engines.
    public static (ZombieSystem System, ReproCollisionWorld World, List<NavFlag> Flags) Session(
        Geometry geometry, ulong seed = 1)
    {
        List<NavFlag> flags = new() { FlatField() };
        var collision = new ReproCollisionWorld(
            ReproTriangles.From(geometry.Build(Vector3.Zero, 512f))!);
        var graph = BakedNavGraph.Build(flags, portalProbe: collision.Resolve);
        var system = new ZombieSystem(new[] { new ZombieTable { Name = "Test", Damage = 5 } },
            new[] { Bound() }, FlatGround, flags)
        {
            MoveResolver = collision.Resolve,
            GroundSnap = collision.GroundSnap,
            VisionBlocked = collision.VisionBlocked,
            PhysicalLineBlocked = collision.PhysicalLineBlocked,
            PathQuery = graph.TryPath,
            PathReady = () => true,
        };
        system.Spawn(new[] { new ZombieSpawnpointData(0, new Vector3(5f, 0f, -5f)) },
            new ReproRandom(seed));
        return (system, collision, flags);
    }

    public static ZombiePlayerView Player(Vector3 position, bool moving = true) =>
        new(1, position, UnturnedGodot.Player.EPlayerStance.Sprint, moving);

    public static ReproCaptureRequest Request(Geometry geometry, Vector3 focus, string note = "") =>
        new()
        {
            Reason = "test",
            Note = note,
            Map = "TestField",
            LevelName = "TestField",
            CapturedAtUtc = "2026-08-03T00:00:00Z",
            Focus = focus,
            Geometry = geometry.Build(focus, 64f),
            Session = new ReproSessionData { ServerTick = 42 },
        };

    public static float TickRate => ServerSimulation.TickRate;
}
