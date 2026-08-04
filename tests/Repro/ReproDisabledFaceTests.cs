using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Repro;
using Xunit;

namespace UnturnedGodot.Tests.Repro;

// The navmesh is baked before the map's objects exist, so the graph a session routes on is the baked
// one MINUS whatever reconciliation probed away — a building's footprint, mostly. A dump that carries
// only the triangles rebuilds a graph the session never had, and answers with a straight line through
// a house. These pin the faces travelling with it, and surviving the slice's renumbering.
public class ReproDisabledFaceTests
{
    // A field of `quads` x `quads` unit cells, two triangles each, so a face index is easy to reason
    // about: cell (x, z) owns triangles 2*(x*quads + z) and +1.
    private static NavFlag Field(int quads)
    {
        var vertices = new List<Vector3>();
        for (int x = 0; x <= quads; x++)
            for (int z = 0; z <= quads; z++)
                vertices.Add(new Vector3(x, 0, z));
        var triangles = new List<int>();
        for (int x = 0; x < quads; x++)
            for (int z = 0; z < quads; z++)
            {
                int a = (x * (quads + 1)) + z, b = a + 1, c = a + quads + 1, d = c + 1;
                triangles.AddRange(new[] { a, b, c, b, d, c });
            }
        return new NavFlag
        {
            Center = new Vector3(quads / 2f, 0, quads / 2f),
            Size = new Vector3(quads + 2f, 10, quads + 2f),
            Vertices = vertices.ToArray(),
            Triangles = triangles.ToArray(),
        };
    }

    [Fact]
    public void SlicingRenumbersTheDisabledFacesWithTheTrianglesTheyName()
    {
        NavFlag flag = Field(8);
        int total = flag.Triangles.Length / 3;

        // Disable one face at each end of the numbering, then slice around the far corner so the low
        // one is dropped and the high one is renumbered. An index carried across verbatim would name
        // whatever face the slice moved into its place — a hole somewhere else, silently.
        var disabled = new HashSet<int> { 0, total - 1 };
        ReproNavFlag whole = ReproCapture.Slice(flag, new Vector3(4, 0, 4), 100f, disabled);
        Assert.Equal(total, whole.Triangles.Length / 3);
        Assert.Equal(new[] { 0, total - 1 }, whole.DisabledFaces); // nothing dropped, nothing moved

        ReproNavFlag corner = ReproCapture.Slice(flag, new Vector3(8, 0, 8), 2f, disabled);
        Assert.True(corner.Sliced);
        int kept = corner.Triangles.Length / 3;
        Assert.InRange(kept, 1, total - 1);
        // Face 0 is at the opposite corner and is gone; the last face is inside the disc and survives,
        // renumbered to the end of what was kept.
        Assert.Equal(new[] { kept - 1 }, corner.DisabledFaces);
    }

    // Every dump written before this field existed has it ABSENT, and the reader leaves an absent
    // array null rather than running the record's initializer — so the replay path meets null, not
    // empty. Constructing a dump in C# runs the initializer and cannot catch that, which is how the
    // first version of this shipped a NullReferenceException on every older dump.
    [Fact]
    public void ADumpFromBeforeTheFieldExisted_StillReplays()
    {
        NavFlag flag = Field(4);
        ReproNavFlag sliced = ReproCapture.Slice(flag, new Vector3(2, 0, 2), 100f);
        var world = new ReproWorldData
        {
            HasNavmesh = true,
            Bounds = { new ReproNavBound { Center = new[] { 2f, 0f, 2f }, Size = new[] { 40f, 20f, 40f } } },
            NavFlags = { sliced with { DisabledFaces = null! } },
        };
        var dump = new ReproDump { World = world };

        var scenario = new ReproScenario(dump);
        scenario.Step();

        Assert.Equal(1, scenario.Tick);
    }

    [Fact]
    public void ADumpsDisabledFacesAreOutOfUseWhenItReplays()
    {
        NavFlag flag = Field(8);

        // A wall of disabled faces across the middle of the field: every triangle of every cell at
        // x = 4. Crossing from x < 4 to x > 4 has to go round the ends, or not at all.
        var wall = new HashSet<int>();
        for (int z = 0; z < 8; z++)
        {
            wall.Add(((4 * 8) + z) * 2);
            wall.Add((((4 * 8) + z) * 2) + 1);
        }

        ReproNavFlag sliced = ReproCapture.Slice(flag, new Vector3(4, 0, 4), 100f, wall);
        Assert.Equal(wall.Count, sliced.DisabledFaces.Length);

        var world = new ReproWorldData { HasNavmesh = true, NavFlags = { sliced } };
        List<NavFlag> flags = ReproZombieState.ToNavFlags(world);

        BakedNavGraph open = BakedNavGraph.Build(flags);
        // Excluded at build time, which is how the session's graph was built and how the replay now
        // rebuilds it: adjacency is derived once, so a face absent from the start can change which
        // edges count as borders, and disabling afterwards cannot add connections the build would have.
        BakedNavGraph walled = BakedNavGraph.Build(flags,
            new Dictionary<NavFlag, HashSet<int>> { [flags[0]] = new(sliced.DisabledFaces) });

        Vector3 from = new(1.5f, 0, 4.5f), to = new(7.5f, 0, 4.5f);
        var straight = new List<Vector3>();
        Assert.True(open.TryPath(from, to, straight));
        Assert.Equal(2, straight.Count); // nothing in the way: string-pulled to a line

        var around = new List<Vector3>();
        bool routed = walled.TryPath(from, to, around);

        // TryPath answers true with a route to the closest REACHABLE point when the destination is cut
        // off, so length alone proves nothing: a short route is the honest "it stopped at the wall",
        // and only a route that actually arrives can have crossed it. Asked the other way round —
        // did it arrive, and if so did it pay for the way round?
        bool arrived = routed && around.Count >= 2
            && new Vector2(around[^1].X - to.X, around[^1].Z - to.Z).Length() < 0.01f;
        if (arrived)
        {
            float length = 0f;
            for (int i = 0; i + 1 < around.Count; i++)
                length += new Vector2(around[i + 1].X - around[i].X,
                    around[i + 1].Z - around[i].Z).Length();
            Assert.True(length > 6.5f,
                $"the route arrived through the disabled wall: {length:F2} m for a 6 m gap");
        }
        else
        {
            // Stopped short, which is what a walled-off destination should give.
            Assert.True(around.Count == 0 || around[^1].X < 4.5f,
                $"the route ended past the wall at {around[^1]} without arriving");
        }
    }
}
