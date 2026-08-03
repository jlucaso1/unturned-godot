using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Repro;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Repro;

// Assembling a dump: what travels whole, what gets cut down to the incident, and what a capture says
// about itself when a piece is missing.
public class ReproCaptureTests
{
    [Fact]
    public void TheNavmeshIsSlicedAroundTheFocusAndRenumbered()
    {
        NavFlag field = ReproWorlds.FlatField();
        int wholeTriangles = field.Triangles.Length / 3;

        ReproNavFlag sliced = ReproCapture.Slice(field, new Vector3(20f, 0f, 20f), radius: 5f);
        int keptTriangles = sliced.Triangles.Length / 3;
        Assert.True(keptTriangles > 0);
        Assert.True(keptTriangles < wholeTriangles / 4,
            $"kept {keptTriangles} of {wholeTriangles} triangles for a 5 m slice");
        Assert.True(sliced.Sliced);

        // Every index has to point inside the slice's own vertex array.
        foreach (int index in sliced.Triangles)
            Assert.InRange(index, 0, (sliced.Vertices.Length / 3) - 1);

        // The flag's box is NOT sliced: checkNavigation and the player's nav region are decided by it.
        Assert.Equal(ReproVector.From(field.Center), sliced.Center);
        Assert.Equal(ReproVector.From(field.Size), sliced.Size);

        // The kept triangles still route: a replay pathing inside the slice gets a real answer.
        var graph = BakedNavGraph.Build(new[]
        {
            new NavFlag
            {
                Center = field.Center,
                Size = field.Size,
                Vertices = ReproZombieState.ToNavFlags(
                    new ReproWorldData { NavFlags = { sliced } })[0].Vertices,
                Triangles = sliced.Triangles,
            },
        });
        var route = new List<Vector3>();
        Assert.True(graph.TryPath(new Vector3(18f, 0f, 18f), new Vector3(22f, 0f, 22f), route));
    }

    [Fact]
    public void AFocusOutsideTheFlagKeepsNothingButTheBox()
    {
        ReproNavFlag sliced = ReproCapture.Slice(ReproWorlds.FlatField(),
            new Vector3(5000f, 0f, 5000f), radius: 5f);
        Assert.Empty(sliced.Triangles);
        Assert.Empty(sliced.Vertices);
        Assert.NotEmpty(sliced.Center);
    }

    [Fact]
    public void AFocusThatCoversEverythingIsNotMarkedSliced()
    {
        ReproNavFlag whole = ReproCapture.Slice(ReproWorlds.FlatField(),
            new Vector3(20f, 0f, 20f), radius: 500f);
        Assert.False(whole.Sliced);
    }

    [Fact]
    public void TheGroundPatchSamplesAroundTheFocusAndMarksGaps()
    {
        ReproHeightPatch patch = ReproCapture.SampleGround(
            (float x, float z, out float y) =>
            {
                y = x + z;
                return x >= 0f; // west of the origin the sampler has no answer
            },
            new Vector3(4f, 0f, 4f), radius: 4f, cell: 1f);

        Assert.Equal(9, patch.Width);
        Assert.Equal(9, patch.Depth);
        Assert.Equal(new[] { 0f, 0f, 0f }, patch.Origin);

        ReproHeightSampler sampler = ReproHeightSampler.From(patch)!;
        Assert.True(sampler.Sample(4f, 4f, out float middle));
        Assert.Equal(8f, middle, 3);
        Assert.True(sampler.Sample(2.5f, 3.5f, out float between)); // bilinear, between samples
        Assert.Equal(6f, between, 3);
        Assert.False(sampler.Sample(100f, 100f, out _)); // off the patch
    }

    [Fact]
    public void ACellWithNoGroundIsNotInterpolatedOver()
    {
        ReproHeightPatch patch = ReproCapture.SampleGround(
            (float x, float z, out float y) =>
            {
                y = 1f;
                return x > 1f;
            },
            new Vector3(2f, 0f, 2f), radius: 2f, cell: 1f);
        ReproHeightSampler sampler = ReproHeightSampler.From(patch)!;
        Assert.False(sampler.Sample(0.5f, 2f, out _));
        Assert.True(sampler.Sample(3.5f, 2f, out _));
    }

    [Fact]
    public void ADegenerateGroundPatchIsNoSampler()
    {
        Assert.Null(ReproHeightSampler.From(null));
        Assert.Null(ReproHeightSampler.From(new ReproHeightPatch { Width = 1, Depth = 1 }));
        Assert.Null(ReproHeightSampler.From(new ReproHeightPatch
        {
            Width = 4,
            Depth = 4,
            Cell = 0f,
        }));
        Assert.Null(ReproHeightSampler.From(new ReproHeightPatch
        {
            Width = 4,
            Depth = 4,
            Cell = 1f,
            Heights = new float[3], // truncated file: fewer samples than the grid claims
        }));
    }

    // The world section is the level the replay rebuilds on, and every part of it has to arrive.
    [Fact]
    public void TheWorldSectionCarriesTheLevel()
    {
        var geometry = new ReproWorlds.Geometry().Ground()
            .Box("pole", new Vector3(12f, 1.5f, 12f), new Vector3(0.25f, 1.5f, 0.25f));
        (ZombieSystem system, _, _) = ReproWorlds.Session(geometry);
        ReproDump dump = ReproCapture.Build(
            ReproWorlds.Request(geometry, new Vector3(12f, 0f, 12f)), system, recorder: null,
            ReproWorlds.FlatGround);

        Assert.True(dump.World.HasNavmesh);
        Assert.Single(dump.World.Bounds);
        Assert.Single(dump.World.Tables);
        Assert.Equal("Test", dump.World.Tables[0].Name);
        Assert.Single(dump.World.NavFlags);
        Assert.True(dump.World.SlicedTriangleCount > 0);
        Assert.NotNull(dump.World.Ground);
        Assert.NotNull(dump.World.Geometry);
        Assert.Equal(42u, dump.Session.ServerTick);
        Assert.Equal(ReproDump.CurrentSchemaVersion, dump.SchemaVersion);

        // Captured without a recorder: state but no history, and the dump says which it is.
        Assert.NotNull(dump.Zombies);
        Assert.Empty(dump.Zombies!.Frames);
        Assert.Contains(dump.Meta.Warnings,
            warning => warning.Contains("no recorder", StringComparison.Ordinal));
    }

    // A capture taken away from every authored navigation flag is a real situation (a map's own player
    // spawn can be hundreds of metres from one), and the dump says why it has no routes.
    [Fact]
    public void AFocusOutsideEveryNavFlagIsCalledOut()
    {
        var geometry = new ReproWorlds.Geometry().Ground();
        (ZombieSystem system, _, _) = ReproWorlds.Session(geometry);
        ReproDump dump = ReproCapture.Build(
            ReproWorlds.Request(geometry, new Vector3(5000f, 0f, 5000f)), system, recorder: null,
            ReproWorlds.FlatGround);
        Assert.True(dump.World.HasNavmesh);
        Assert.Equal(0, dump.World.SlicedTriangleCount);
        Assert.Contains(dump.Meta.Warnings,
            warning => warning.Contains("outside every navigation flag", StringComparison.Ordinal));
    }

    [Fact]
    public void AMapWithNoNavmeshSaysSo()
    {
        var system = new ZombieSystem(new[] { new ZombieTable { Name = "Test", Damage = 5 } },
            new[] { ReproWorlds.Bound() }, ReproWorlds.FlatGround);
        ReproDump dump = ReproCapture.Build(
            ReproWorlds.Request(new ReproWorlds.Geometry(), Vector3.Zero), system, recorder: null,
            ground: null);
        Assert.False(dump.World.HasNavmesh);
        Assert.Empty(dump.World.NavFlags);
        Assert.Null(dump.World.Ground);
    }

    // With no simulation at all a capture is still a valid dump — the point of the sections being
    // independent is that a subsystem that has nothing to say does not break the file.
    [Fact]
    public void ACaptureWithNoSimulationIsStillADump()
    {
        ReproDump dump = ReproCapture.Build(
            ReproWorlds.Request(new ReproWorlds.Geometry(), Vector3.Zero), system: null,
            recorder: null);
        Assert.Null(dump.Zombies);
        Assert.Empty(dump.World.Bounds);
        Assert.NotNull(ReproDump.FromJson(dump.ToJson()));
        Assert.Contains("no navmesh", dump.Describe(), StringComparison.Ordinal);
    }

    // Whatever a future subsystem hands the capture rides along untouched, without this file or any
    // reader of it having to know what it is.
    [Fact]
    public void AnUnknownSectionRidesAlong()
    {
        var request = new ReproCaptureRequest
        {
            Sections =
            {
                ["weather"] = JsonDocument.Parse("""{"rain":0.5,"wind":[1,0,0]}""").RootElement,
            },
            Log = new[] { "[  1.000] [weather] rain started" },
        };
        ReproDump parsed = ReproDump.FromJson(
            ReproCapture.Build(request, system: null, recorder: null).ToJson())!;
        Assert.Equal(0.5, parsed.Sections["weather"].GetProperty("rain").GetDouble());
        Assert.Single(parsed.Log);
        Assert.Contains("section     weather", parsed.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void NullsAreRefused()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ReproCapture.Build(null!, system: null, recorder: null));
        Assert.Throws<ArgumentNullException>(() =>
            ReproCapture.BuildWorld(system: null, null!, ground: null));
        Assert.Throws<ArgumentNullException>(() => ReproCapture.Slice(null!, Vector3.Zero, 1f));
        Assert.Throws<ArgumentNullException>(() =>
            ReproCapture.SampleGround(null!, Vector3.Zero, 1f, 1f));
    }

    [Fact]
    public void ReadingSomethingThatIsNotADumpFails()
    {
        string path = System.IO.Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllText(path, "null");
            Assert.Throws<System.IO.InvalidDataException>(() => ReproDump.Read(path));
            System.IO.File.WriteAllText(path, "{ not json");
            Assert.Throws<JsonException>(() => ReproDump.Read(path));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
