using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Repro;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Repro;

// The edges. A bug-repro harness is used exactly when something has already gone wrong, so the
// degenerate inputs — a dump with nothing in it, a query where the world has no geometry, a body that
// starts inside a wall — are not hypothetical: they are the day it gets used.
public class ReproEdgeTests
{
    [Fact]
    public void ADumpWithNoSimulationSectionStillReplays()
    {
        var scenario = new ReproScenario(new ReproDump());
        ReproReplayReport report = scenario.Run(extraTicks: 3);
        Assert.Equal(0, scenario.WindowTicks);
        Assert.Equal(3, report.Ticks);
        Assert.Equal(0, report.ComparedSamples);
        Assert.Equal(0f, report.MeanPositionError);
        Assert.False(report.ReproducesRecording);
        Assert.Contains("diverges", report.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void ADumpFromAMapWithoutANavmeshReplaysWithoutOne()
    {
        var dump = new ReproDump
        {
            World = new ReproWorldData { HasNavmesh = false, Bounds = { Box() } },
            Zombies = new ReproZombieSection(),
        };
        Assert.Null(new ReproScenario(dump).System.PathQuery);

        // And with neither source of answers, the world delegates are not installed at all: a resolver
        // where the session had none is a different simulation, not a less detailed one.
        var bare = new ReproScenario(dump, new ReproScenarioOptions
        {
            UseRecordedAnswers = false,
            UseGeometry = false,
        });
        Assert.Null(bare.System.MoveResolver);
        Assert.Null(bare.System.GroundSnap);
        Assert.Null(bare.System.VisionBlocked);
        Assert.Null(bare.System.PhysicalLineBlocked);
    }

    // A session and a replay can disagree about ROUTING alone: the live map had the full reconciled
    // graph where a self-contained replay may have only the dump's local slice. Turning the whole oracle
    // off also changes ground and collision, so the two causes cannot be told apart afterwards.
    // RecomputePaths drops the recorded routes and keeps everything else.
    [Fact]
    public void RecomputingPathsDropsTheRecordedRoutesAndKeepsTheRest()
    {
        // One recorded answer: a route from (0,0,0) to (10,0,0) that detours through (0,0,9). Nothing
        // a pathfinder over open ground would ever return, so which source answered is unambiguous.
        var oracle = new ReproOracleData();
        oracle.Path.AddRange(new[]
        {
            0f, 1f,          // tick, zombie
            0f, 0f, 0f,      // from
            10f, 0f, 0f,     // to
            BakedNavGraph.AgentRadius,
            1f,              // found
            0f, 2f,          // first point, count
        });
        oracle.PathPoints.AddRange(new[] { 0f, 0f, 9f, 10f, 0f, 0f });

        var dump = new ReproDump
        {
            World = new ReproWorldData { HasNavmesh = true, Bounds = { Box() } },
            Zombies = new ReproZombieSection
            {
                Oracle = oracle,
                Frames = { new ReproFrame { Dt = 0.08f } },
            },
        };

        var recorded = new List<Vector3>();
        Assert.True(new ReproScenario(dump).System.PathQuery!(
            new Vector3(0, 0, 0), new Vector3(10, 0, 0), recorded, BakedNavGraph.AgentRadius));
        Assert.Equal(new Vector3(0, 0, 9), recorded[0]); // the session's own answer, detour and all

        var fresh = new List<Vector3>();
        var scenario = new ReproScenario(dump, new ReproScenarioOptions
        {
            RecomputePaths = true,
            NavFlags = new[] { ReproWorlds.FlatField() },
        });
        Assert.True(scenario.System.PathQuery!(new Vector3(0, 0, 0), new Vector3(10, 0, 0), fresh,
            BakedNavGraph.AgentRadius)); // it answered, rather than failing into an empty list
        Assert.DoesNotContain(new Vector3(0, 0, 9), fresh); // recomputed: the detour is not repeated

        // And the rest of the oracle still answers — this is a routing switch, not `--no-oracle`.
        // Readiness is the one exception, and deliberately so: a recording that says the map was not
        // answering yet would drop the follower onto its direct fallback and the recomputed routes
        // would never be asked for.
        Assert.NotNull(scenario.Oracle);
        Assert.True(scenario.System.PathReady!());
    }

    [Fact]
    public void ReconcilePathsAppliesTheCurrentCollisionRuleInsteadOfTheRecordedFaceList()
    {
        // A one-metre sill cuts the middle of a baked flat field. With no recorded disabled faces the
        // ordinary replay routes straight through it; current reconciliation must carve that band and
        // leave a route around either open end.
        ReproWorlds.Geometry geometry = new ReproWorlds.Geometry().Ground()
            .Box("sill", new Vector3(20f, 0.5f, 20f), new Vector3(0.5f, 0.5f, 10f));
        var dump = new ReproDump
        {
            World = new ReproWorldData
            {
                HasNavmesh = true,
                Bounds = { Box() },
                Geometry = geometry.Build(new Vector3(20f, 0f, 20f), 64f),
            },
            Zombies = new ReproZombieSection
            {
                Seams = new ReproWorldSeams { PathQuery = true },
            },
        };
        var scenario = new ReproScenario(dump, new ReproScenarioOptions
        {
            RecomputePaths = true,
            ReconcilePaths = true,
            NavFlags = new[] { ReproWorlds.FlatField() },
        });
        var path = new List<Vector3>();

        Assert.True(scenario.System.PathQuery!(new Vector3(5f, 0f, 20f),
            new Vector3(35f, 0f, 20f), path, BakedNavGraph.AgentRadius));
        Assert.Contains(path, point => point.Z < 10f || point.Z > 30f);
    }

    [Fact]
    public void ReconcilePathsReenablesARecordedFaceProvenWalkableByCapturedGeometry()
    {
        NavFlag flag = ReproWorlds.FlatField();
        const int Quads = 40;
        var stale = new HashSet<int>
        {
            2 * ((20 * Quads) + 0),
            (2 * ((20 * Quads) + 0)) + 1,
        };
        ReproNavFlag captured = ReproCapture.Slice(flag, new Vector3(20f, 0f, 20f),
            radius: 100f, disabled: stale);
        ReproWorlds.Geometry geometry = new ReproWorlds.Geometry().Ground();
        ReproGeometryData capturedGeometry = geometry.Build(new Vector3(20f, 0f, 20f), 64f);
        ReproScenario scenario = ReconciledScenario(captured, capturedGeometry);
        ReproScenario current = ReconciledScenario(
            captured with { DisabledFaces = Array.Empty<int>() }, capturedGeometry);
        var path = new List<Vector3>();
        var expected = new List<Vector3>();

        Assert.True(scenario.System.PathQuery!(new Vector3(5f, 0f, 0.25f),
            new Vector3(35f, 0f, 0.25f), path, BakedNavGraph.AgentRadius));
        Assert.True(current.System.PathQuery!(new Vector3(5f, 0f, 0.25f),
            new Vector3(35f, 0f, 0.25f), expected, BakedNavGraph.AgentRadius));
        Assert.Equal(expected, path); // the stale exclusion was replaced by the same current verdict
    }

    [Fact]
    public void ReconcilePathsPreservesARecordedExclusionOutsideTheCollisionSlice()
    {
        NavFlag flag = ReproWorlds.FlatField();
        const int Quads = 40;
        var recorded = new HashSet<int>
        {
            2 * ((20 * Quads) + 0),
            (2 * ((20 * Quads) + 0)) + 1,
        };
        ReproNavFlag captured = ReproCapture.Slice(flag, new Vector3(20f, 0f, 20f),
            radius: 100f, disabled: recorded);
        ReproWorlds.Geometry geometry = new ReproWorlds.Geometry().Ground();
        ReproGeometryData capturedGeometry = geometry.Build(Vector3.Zero, radius: 5f);
        ReproScenario scenario = ReconciledScenario(captured, capturedGeometry);
        ReproScenario withoutRecordedFace = ReconciledScenario(
            captured with { DisabledFaces = Array.Empty<int>() }, capturedGeometry);
        var path = new List<Vector3>();
        var open = new List<Vector3>();

        Assert.True(scenario.System.PathQuery!(new Vector3(5f, 0f, 0.25f),
            new Vector3(35f, 0f, 0.25f), path, BakedNavGraph.AgentRadius));
        Assert.True(withoutRecordedFace.System.PathQuery!(new Vector3(5f, 0f, 0.25f),
            new Vector3(35f, 0f, 0.25f), open, BakedNavGraph.AgentRadius));
        Assert.False(open.SequenceEqual(path),
            "the out-of-slice recorded exclusion was incorrectly re-enabled");
    }

    private static ReproScenario ReconciledScenario(ReproNavFlag flag,
        ReproGeometryData geometry)
    {
        var dump = new ReproDump
        {
            World = new ReproWorldData
            {
                HasNavmesh = true,
                Bounds = { Box() },
                NavFlags = { flag },
                Geometry = geometry,
            },
            Zombies = new ReproZombieSection
            {
                Seams = new ReproWorldSeams { PathQuery = true },
            },
        };
        return new ReproScenario(dump, new ReproScenarioOptions
        {
            RecomputePaths = true,
            ReconcilePaths = true,
        });
    }

    // Handing the replay the real map's navmesh (or someone else's pathfinder) is what lifts it out of
    // the dump's local slice.
    [Fact]
    public void AProvidedPathfinderIsUsedInsteadOfTheSlice()
    {
        var dump = new ReproDump
        {
            World = new ReproWorldData
            {
                HasNavmesh = true,
                Bounds = { Box() },
                Tables = { new ReproZombieTable { Name = "Test", Damage = 5 } },
            },
            Zombies = new ReproZombieSection
            {
                State = new ReproSystemState
                {
                    Zombies =
                    {
                        new ReproZombieRecord
                        {
                            Id = 1,
                            Position = new[] { 10f, 0f, 10f },
                            State = EZombieState.Chase,
                            Target = 1,
                        },
                    },
                },
                Frames =
                {
                    new ReproFrame
                    {
                        Dt = 0.08f,
                        Players =
                        {
                            new ReproPlayerSample
                            {
                                Id = 1,
                                Position = new[] { 20f, 0f, 20f },
                                Stance = UnturnedGodot.Player.EPlayerStance.Sprint,
                            },
                        },
                    },
                },
            },
        };

        int asked = 0;
        var scenario = new ReproScenario(dump, new ReproScenarioOptions
        {
            NavFlags = new[] { ReproWorlds.FlatField() },
            PathQuery = (from, to, path, radius) =>
            {
                asked++;
                path.Clear();
                path.Add(to);
                return true;
            },
        });
        ReproReplayReport report = scenario.Run(extraTicks: 5);
        Assert.True(asked > 0);
        Assert.True(scenario.GeometryAnswers > 0);
        // Recomputed routes are called out on their own: a local graph slice cannot always reproduce
        // the live full-map route faithfully.
        Assert.Equal(asked, report.RoutesRecomputed);
        Assert.Contains("routes           ", report.Describe(), StringComparison.Ordinal);
    }

    private static ReproNavBound Box() => new()
    {
        Center = new[] { 20f, 0f, 20f },
        Size = new[] { 200f, 100f, 200f },
        MaxZombies = 8,
        SpawnZombies = true,
    };

    // Every number the report prints, from a report built by hand — the shapes a replay produces on a
    // bad day, without having to arrange a bad day.
    [Fact]
    public void TheReportDescribesDivergenceAndSpinning()
    {
        var report = new ReproReplayReport
        {
            Ticks = 10,
            ComparedSamples = 20,
            PositionSamples = 4,
            SilenceChecks = 16,
            MaxPositionError = 2f,
            SumPositionError = 4f,
            MaxYawError = 30f,
            StateMismatches = 1,
            OracleHits = 5,
            OracleMisses = 2,
            GeometryAnswers = 3,
            UnansweredQueries = 1,
        };
        report.Motion.Add(new ReproMotionSummary
        {
            Id = 7,
            TravelledMetres = 20f,
            NetDisplacementMetres = 0.2f,
            YawChurnDegrees = 900f,
            FinalState = EZombieState.Chase,
        });
        // The mean is over the four samples that carried a position, not over the sixteen idle
        // zombies that could not move the number in either direction.
        Assert.Equal(1f, report.MeanPositionError);
        Assert.False(report.ReproducesRecording);
        string text = report.Describe();
        Assert.Contains("spinning in place", text, StringComparison.Ordinal);
        Assert.Contains("state mismatches 1", text, StringComparison.Ordinal);

        // A clean one reads the other way.
        var clean = new ReproReplayReport
        {
            ComparedSamples = 1,
            PositionSamples = 1,
            MaxPositionError = 0.001f,
        };
        Assert.True(clean.ReproducesRecording);
        Assert.Contains("reproduces the recording", clean.Describe(), StringComparison.Ordinal);

        // Each condition on its own is enough to fail the claim.
        Assert.False(new ReproReplayReport { ComparedSamples = 1, MaxYawError = 5f }
            .ReproducesRecording);
        Assert.False(new ReproReplayReport { ComparedSamples = 1, StateMismatches = 1 }
            .ReproducesRecording);
    }

    [Fact]
    public void AReplayThatEndsInADifferentStateCountsIt()
    {
        var dump = new ReproDump
        {
            World = new ReproWorldData { Bounds = { Box() } },
            Zombies = new ReproZombieSection
            {
                State = new ReproSystemState
                {
                    Zombies = { new ReproZombieRecord { Id = 3, Position = new[] { 1f, 0f, 1f } } },
                },
                Frames =
                {
                    new ReproFrame
                    {
                        Dt = 0.08f,
                        Zombies =
                        {
                            new ReproMotionSample
                            {
                                Id = 3,
                                Position = new[] { 5f, 0f, 5f },
                                State = EZombieState.Chase,
                            },
                        },
                    },
                },
            },
        };
        ReproReplayReport report = new ReproScenario(dump).Run();
        Assert.Equal(1, report.StateMismatches);
        Assert.True(report.MaxPositionError > 5f);
    }

    [Fact]
    public void TheSectionSummaryCountsWhatWasHappening()
    {
        var section = new ReproZombieSection
        {
            State = new ReproSystemState
            {
                RandomIsReproducible = false,
                Zombies =
                {
                    new ReproZombieRecord { Id = 1, State = EZombieState.Chase, Target = 2 },
                    new ReproZombieRecord { Id = 2, State = EZombieState.Idle, Target = byte.MaxValue },
                },
            },
        };
        string text = section.Describe();
        Assert.Contains("2 (1 awake, 1 hunting)", text, StringComparison.Ordinal);
        Assert.Contains("non-reproducible RNG", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDumpSummaryShowsTheCameraAndTheWarnings()
    {
        var dump = new ReproDump
        {
            Meta = new ReproMeta
            {
                ShotCam = "-616.22,35.02,-66.58,-24.59,-95.1",
                Warnings = { "the collision slice hit its ceiling" },
                FocusPoint = new[] { -616.22f, 35.02f, -66.58f },
            },
        };
        string text = dump.Describe();
        Assert.Contains("SHOT_CAM=-616.22,35.02,-66.58,-24.59,-95.1", text, StringComparison.Ordinal);
        Assert.Contains("! the collision slice hit its ceiling", text, StringComparison.Ordinal);
        Assert.Contains("(-616.22, 35.02, -66.58)", text, StringComparison.Ordinal);

        // The indented form is the same document, just readable.
        Assert.Contains("\n", dump.ToJson(pretty: true), StringComparison.Ordinal);
        Assert.NotNull(ReproDump.FromJson(dump.ToJson(pretty: true)));
    }

    [Fact]
    public void TwoShortVectorsDescribeAsNothing() =>
        Assert.Equal("(none)", ReproVector.Describe(new[] { 1f, 2f }));

    [Fact]
    public void AnOwnerOutsideTheTableIsUnknown()
    {
        ReproTriangles triangles = ReproTriangles.From(
            new ReproWorlds.Geometry().Ground().Build(Vector3.Zero, 512f))!;
        Assert.Equal("?", triangles.OwnerOf(-1));
        Assert.Equal("ground", triangles.OwnerOf(0));
    }

    [Fact]
    public void TheGridRejectsEveryDirectionOfOutside()
    {
        ReproTriangles triangles = ReproTriangles.From(new ReproWorlds.Geometry()
            .Box("pole", new Vector3(0f, 1.5f, 0f), new Vector3(0.5f, 1.5f, 0.5f))
            .Build(Vector3.Zero, 512f))!;
        var found = new List<int>();
        foreach ((float x, float z) in new[] { (-50f, 0f), (50f, 0f), (0f, -50f), (0f, 50f) })
        {
            triangles.Gather(x - 1f, z - 1f, x + 1f, z + 1f, found);
            Assert.Empty(found);
        }
        triangles.Gather(-1f, -1f, 1f, 1f, found);
        Assert.NotEmpty(found);
    }

    [Fact]
    public void ACollisionWorldNeedsGeometryToExist()
    {
        Assert.Null(ReproCollisionWorld.From(null));
        Assert.Null(ReproCollisionWorld.From(new ReproWorldData()));
        ReproCollisionWorld world = ReproCollisionWorld.From(new ReproWorldData
        {
            Geometry = new ReproWorlds.Geometry().Ground().Build(Vector3.Zero, 512f),
        })!;
        Assert.Equal(2, world.TriangleCount);
    }

    // Nothing anywhere near: the sweep finds no candidates at all and delivers the whole step.
    [Fact]
    public void AStepWhereThereIsNoGeometryIsFree()
    {
        ReproCollisionWorld world = new(ReproTriangles.From(new ReproWorlds.Geometry()
            .Box("pole", new Vector3(0f, 1.5f, 0f), new Vector3(0.5f, 1.5f, 0.5f))
            .Build(Vector3.Zero, 512f))!);
        Vector3 to = new(101f, 0f, 100f);
        Assert.True(world.Resolve(new Vector3(100f, 0f, 100f), to, 0.4f).IsEqualApprox(to));
        Assert.False(world.GroundSnap(new Vector3(100f, 0f, 100f), out _));
    }

    // A body that starts inside geometry stops where it is rather than being pushed out: a single
    // CastMotion cannot depenetrate either, and inventing a shove would be a difference from the
    // session, not a fidelity improvement.
    [Fact]
    public void ABodyThatStartsInsideAWallDoesNotAdvance()
    {
        ReproCollisionWorld world = new(ReproTriangles.From(new ReproWorlds.Geometry().Ground()
            .Box("wall", new Vector3(10f, 1.5f, 0f), new Vector3(0.1f, 1.5f, 20f))
            .Build(Vector3.Zero, 512f))!);
        Vector3 from = new(10f, 0f, 0f);
        Vector3 result = world.Resolve(from, from + new Vector3(0.5f, 0f, 0f), 0.4f);
        Assert.True(result.X <= from.X + 0.001f, $"pushed through from inside: {result}");
    }

    // A wall the body is exactly coplanar with: the closest points coincide, so there is no direction
    // between them and the surface's own normal is what the slide has to use.
    [Fact]
    public void AContactWithNoDirectionFallsBackToTheFaceNormal()
    {
        var geometry = new ReproWorlds.Geometry();
        geometry.Triangle("plane", CollisionLayers.World,
            new Vector3(10f, 0f, -5f), new Vector3(10f, 4f, -5f), new Vector3(10f, 0f, 5f));
        ReproCollisionWorld world = new(ReproTriangles.From(geometry.Build(Vector3.Zero, 512f))!);
        Vector3 result = world.Resolve(new Vector3(10f, 0f, 0f), new Vector3(10f, 0f, 1f), 0.4f);
        Assert.True(float.IsFinite(result.X) && float.IsFinite(result.Z), result.ToString());
    }

    [Theory]
    [InlineData(-1f, 0f)]    // west of the patch
    [InlineData(0f, -1f)]    // north of it
    [InlineData(100f, 0f)]   // east
    [InlineData(0f, 100f)]   // south
    public void TheHeightPatchRefusesEverythingOutsideIt(float x, float z)
    {
        ReproHeightSampler sampler = ReproHeightSampler.From(ReproCapture.SampleGround(
            ReproWorlds.FlatGround, new Vector3(4f, 0f, 4f), radius: 4f, cell: 1f))!;
        Assert.False(sampler.Sample(x, z, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AGapAtAnyCornerStopsTheInterpolation(int corner)
    {
        var patch = new ReproHeightPatch
        {
            Origin = new[] { 0f, 0f, 0f },
            Cell = 1f,
            Width = 2,
            Depth = 2,
            Heights = new[] { 1f, 1f, 1f, 1f },
        };
        patch.Heights[corner] = float.NaN;
        Assert.False(ReproHeightSampler.From(patch)!.Sample(0.5f, 0.5f, out _));
    }

    [Fact]
    public void ADegenerateHeightPatchIsNoSampler()
    {
        Assert.Null(ReproHeightSampler.From(new ReproHeightPatch { Width = 1, Depth = 4, Cell = 1f }));
        Assert.Null(ReproHeightSampler.From(new ReproHeightPatch { Width = 4, Depth = 1, Cell = 1f }));
    }

    // Queries the recorder sees outside any tick — a probe, or the host's own lookup — are not part of
    // the simulation and are left out of the window rather than attributed to whatever ticked last.
    [Fact]
    public void QueriesOutsideATickAreNotRecorded()
    {
        var geometry = new ReproWorlds.Geometry().Ground();
        (ZombieSystem system, _, _) = ReproWorlds.Session(geometry);
        var recorder = new ReproRecorder(system, new ReproRecorderOptions { WindowTicks = 4 });
        recorder.Attach();

        system.MoveResolver!(Vector3.Zero, Vector3.One, 0.4f);
        system.GroundSnap!(Vector3.Zero, out _);
        system.VisionBlocked!(Vector3.Zero, Vector3.One);
        system.PathQuery!(Vector3.Zero, Vector3.One, new List<Vector3>(), 0.4f);
        recorder.EndTick(system); // an end with no begin: nothing to attribute it to

        for (int i = 0; i < 4; i++)
            system.Tick(new[] { ReproWorlds.Player(new Vector3(10f, 0f, 10f)) }, ReproWorlds.TickRate);
        ReproZombieSection section = recorder.Build(out _, out _);
        Assert.All(section.Oracle.Move, value => Assert.True(float.IsFinite(value)));
        Assert.True(section.Oracle.MoveCount > 0);
    }

    // A query made during a tick but outside any one zombie's turn (the host asking something of its
    // own) is recorded, with no zombie attributed to it.
    [Fact]
    public void AQueryBetweenZombiesIsRecordedWithoutAnOwner()
    {
        var geometry = new ReproWorlds.Geometry().Ground();
        (ZombieSystem system, _, _) = ReproWorlds.Session(geometry);
        // Chained BEHIND the recorder: it runs inside the tick, after the recorder has opened it.
        system.Observer = new AsksDuringTheTick(system);
        var recorder = new ReproRecorder(system, new ReproRecorderOptions { WindowTicks = 4 });
        recorder.Attach();

        for (int i = 0; i < 4; i++)
            system.Tick(new[] { ReproWorlds.Player(new Vector3(10f, 0f, 10f)) }, ReproWorlds.TickRate);

        ReproZombieSection section = recorder.Build(out _, out _);
        var owners = new List<float>();
        for (int i = 1; i < section.Oracle.Ground.Count; i += ReproOracleData.GroundStride)
            owners.Add(section.Oracle.Ground[i]);
        Assert.Contains(-1f, owners);
    }

    // Nothing is kept when the ceiling is zero, and the drop count says exactly that.
    [Fact]
    public void AZeroCeilingRecordsNothing()
    {
        var geometry = new ReproWorlds.Geometry().Ground();
        (ZombieSystem system, _, _) = ReproWorlds.Session(geometry);
        var recorder = new ReproRecorder(system,
            new ReproRecorderOptions { WindowTicks = 4, MaxCallsPerTick = 0 });
        recorder.Attach();
        for (int i = 0; i < 8; i++)
            system.Tick(new[] { ReproWorlds.Player(new Vector3(10f, 0f, 10f)) }, ReproWorlds.TickRate);

        ReproZombieSection section = recorder.Build(out _, out _);
        Assert.Equal(0, section.Oracle.MoveCount);
        Assert.Equal(0, section.Oracle.GroundCount);
        Assert.Equal(0, section.Oracle.VisionCount);
        Assert.Equal(0, section.Oracle.PathCount);
        Assert.True(recorder.DroppedCalls > 0);
    }

    // Only the zombies that went away are forgotten.
    [Fact]
    public void OnlyVanishedZombiesLoseTheirTrack()
    {
        var geometry = new ReproWorlds.Geometry().Ground();
        (ZombieSystem system, _, _) = ReproWorlds.Session(geometry);
        ZombieInstance survivor = system.Zombies[0];
        survivor.State = EZombieState.Chase;

        ZombieSystemState twoOfThem = system.CaptureState();
        ZombieInstance second = ZombieSystemState.Clone(survivor);
        second.Id = 99;
        twoOfThem.Zombies.Add(second);
        system.RestoreState(twoOfThem);

        var watcher = new ReproWatcher(new ReproWatcherOptions { WindowSeconds = 0.1f });
        watcher.Poll(system, 0.08f, out _, out _);
        watcher.Poll(system, 0.08f, out _, out _);

        ZombieSystemState oneOfThem = system.CaptureState();
        oneOfThem.Zombies.RemoveAt(1);
        system.RestoreState(oneOfThem);
        Assert.False(watcher.Poll(system, 0.08f, out _, out _));
        Assert.False(watcher.Poll(system, 0.08f, out _, out _));
    }

    // A dump with no frames at all still has to answer "how long is a tick", and a frame that never
    // recorded its own dt falls back to the server's rate rather than stepping by zero.
    [Fact]
    public void AMissingTickRateFallsBackToTheServerRate()
    {
        var withoutRate = new ReproDump
        {
            Meta = new ReproMeta { TickRate = 0f },
            Zombies = new ReproZombieSection(),
        };
        Assert.Equal(3, new ReproScenario(withoutRate).Run(extraTicks: 3).Ticks);

        var withoutDt = new ReproDump
        {
            World = new ReproWorldData { Bounds = { Box() } },
            Zombies = new ReproZombieSection
            {
                State = new ReproSystemState
                {
                    Zombies = { new ReproZombieRecord { Id = 1, State = EZombieState.Return } },
                },
                Frames = { new ReproFrame { Dt = 0f } },
            },
        };
        var scenario = new ReproScenario(withoutDt);
        scenario.Run();
        Assert.Equal(1, scenario.Tick);
    }

    private sealed class AsksDuringTheTick : IZombieTickObserver
    {
        private readonly ZombieSystem _system;

        public AsksDuringTheTick(ZombieSystem system) => _system = system;

        public void BeginTick(ZombieSystem system, IReadOnlyList<ZombiePlayerView> players, float dt) =>
            _system.GroundSnap!(new Vector3(3f, 0f, 3f), out _);

        public void EndTick(ZombieSystem system)
        {
        }
    }

    // Who a zombie hunts is behaviour, and a build can get it wrong without moving: with two players
    // in reach, retargeting the other one looks identical in position and state for a short window.
    [Fact]
    public void AZombieHuntingTheWrongPlayerIsNotAReproduction()
    {
        var dump = new ReproDump
        {
            World = new ReproWorldData { Bounds = { Box() } },
            Zombies = new ReproZombieSection
            {
                State = new ReproSystemState
                {
                    Zombies =
                    {
                        new ReproZombieRecord
                        {
                            Id = 5,
                            Position = new[] { 20f, 0f, 20f },
                            State = EZombieState.Idle,
                            Target = byte.MaxValue,
                        },
                    },
                },
                Frames =
                {
                    new ReproFrame
                    {
                        Dt = 0.08f,
                        Zombies =
                        {
                            new ReproMotionSample
                            {
                                Id = 5,
                                Position = new[] { 20f, 0f, 20f },
                                State = EZombieState.Idle,
                                Target = 7, // the recording says it was hunting player 7
                            },
                        },
                    },
                },
            },
        };
        ReproReplayReport report = new ReproScenario(dump).Run();
        Assert.Equal(0f, report.MaxPositionError);
        Assert.Equal(0, report.StateMismatches);
        Assert.Equal(1, report.TargetMismatches);
        Assert.False(report.ReproducesRecording);
        Assert.Contains("target mismatches 1", report.Describe(), StringComparison.Ordinal);
    }

    // The first tick counts. Measured from the pose the tick already produced, a zombie that turns
    // hard on tick one contributes nothing to the summary — and a one-tick window measures nothing
    // at all, which is exactly when the summary is load-bearing.
    [Fact]
    public void MotionIsMeasuredFromTheRestoredPoseNotTheFirstStep()
    {
        var geometry = new ReproWorlds.Geometry().Ground();
        (ZombieSystem system, _, _) = ReproWorlds.Session(geometry);
        var recorder = new ReproRecorder(system, new ReproRecorderOptions { WindowTicks = 4 });
        recorder.Attach();
        for (int i = 0; i < 6; i++)
            system.Tick(new[] { ReproWorlds.Player(new Vector3(10f, 0f, 10f)) }, ReproWorlds.TickRate);
        ReproDump dump = ReproCapture.Build(
            ReproWorlds.Request(geometry, new Vector3(10f, 0f, 10f)), system, recorder,
            ReproWorlds.FlatGround);

        var scenario = new ReproScenario(dump);
        Vector3 start = scenario.System.Zombies[0].Position;
        ReproReplayReport report = scenario.Run();

        ReproMotionSummary summary = Assert.Single(report.Motion);
        float actual = (scenario.System.Zombies[0].Position - start).Length();
        // Every tick of the window is in the summary, including the first one.
        Assert.Equal(actual, summary.NetDisplacementMetres, 3);
        Assert.True(summary.TravelledMetres >= summary.NetDisplacementMetres);
        Assert.True(summary.TravelledMetres > 0f);
    }

    // A verdict has to account for the world it could not answer for. A window where part of the world
    // was treated as empty is not a reproduction, however well the positions happen to line up.
    [Fact]
    public void AWindowWithUnansweredQueriesIsNotAReproduction()
    {
        var geometry = new ReproWorlds.Geometry().Ground();
        (ZombieSystem system, _, _) = ReproWorlds.Session(geometry);
        var recorder = new ReproRecorder(system, new ReproRecorderOptions { WindowTicks = 8 });
        recorder.Attach();
        for (int i = 0; i < 8; i++)
            system.Tick(new[] { ReproWorlds.Player(new Vector3(10f, 0f, 10f)) }, ReproWorlds.TickRate);

        // A dump whose recording is complete AND has geometry reproduces...
        ReproDump whole = ReproCapture.Build(
            ReproWorlds.Request(geometry, new Vector3(10f, 0f, 10f)), system, recorder,
            ReproWorlds.FlatGround);
        Assert.True(new ReproScenario(whole).Run().ReproducesRecording);

        // ...and the same dump with nothing to answer the queries the recording does not cover does
        // not, even though the recorded answers still put every zombie exactly where it was.
        var holed = new ReproDump
        {
            Meta = whole.Meta,
            World = new ReproWorldData
            {
                HasNavmesh = whole.World.HasNavmesh,
                Bounds = whole.World.Bounds,
                Tables = whole.World.Tables,
                NavFlags = whole.World.NavFlags,
            },
            Zombies = Missing(whole.Zombies!),
        };
        ReproReplayReport report = new ReproScenario(holed).Run();
        Assert.True(report.UnansweredInWindow > 0);
        Assert.False(report.ReproducesRecording);
        Assert.Contains("inside the window", report.Describe(), StringComparison.Ordinal);
    }

    // The same section with the ground answers removed: what a recording that hit the per-tick
    // ceiling looks like from the replay's side.
    private static ReproZombieSection Missing(ReproZombieSection section)
    {
        var oracle = new ReproOracleData();
        oracle.Move.AddRange(section.Oracle.Move);
        oracle.Vision.AddRange(section.Oracle.Vision);
        oracle.Path.AddRange(section.Oracle.Path);
        oracle.PathPoints.AddRange(section.Oracle.PathPoints);
        oracle.Ready.AddRange(section.Oracle.Ready);
        var copy = new ReproZombieSection { State = section.State, Oracle = oracle };
        copy.Frames.AddRange(section.Frames);
        return copy;
    }

    // Beyond the window there is nothing to reproduce, so questions nobody recorded are expected and
    // must not retroactively disqualify the window that did reproduce.
    [Fact]
    public void UnansweredQueriesPastTheWindowDoNotChangeTheVerdict()
    {
        var geometry = new ReproWorlds.Geometry().Ground();
        (ZombieSystem system, _, _) = ReproWorlds.Session(geometry);
        var recorder = new ReproRecorder(system, new ReproRecorderOptions { WindowTicks = 8 });
        recorder.Attach();
        for (int i = 0; i < 8; i++)
            system.Tick(new[] { ReproWorlds.Player(new Vector3(10f, 0f, 10f)) }, ReproWorlds.TickRate);
        ReproDump dump = ReproCapture.Build(
            ReproWorlds.Request(geometry, new Vector3(10f, 0f, 10f)), system, recorder,
            ReproWorlds.FlatGround);

        ReproReplayReport report =
            new ReproScenario(dump, new ReproScenarioOptions { UseGeometry = false }).Run(extraTicks: 30);
        Assert.True(report.UnansweredQueries > 0);
        Assert.Equal(0, report.UnansweredInWindow);
        Assert.True(report.ReproducesRecording, report.Describe());
    }

    // A dedicated server has a pathfinder and no physics at all. Replaying one of its dumps with a
    // resolver, a ground snap and a vision ray installed is not a more faithful reproduction — the
    // brain takes a different branch for each one — and it would fail its own verification.
    [Fact]
    public void ADumpFromAWorldWithoutPhysicsReplaysWithoutPhysics()
    {
        var flags = new List<NavFlag> { ReproWorlds.FlatField() };
        var graph = BakedNavGraph.Build(flags);
        var system = new ZombieSystem(new[] { new ZombieTable { Name = "Test", Damage = 5 } },
            new[] { ReproWorlds.Bound() }, ReproWorlds.FlatGround, flags)
        {
            PathQuery = graph.TryPath,
            PathReady = () => true,
        };
        system.Spawn(new[] { new ZombieSpawnpointData(0, new Vector3(5f, 0f, -5f)) },
            new ReproRandom(1));
        var recorder = new ReproRecorder(system, new ReproRecorderOptions { WindowTicks = 16 });
        recorder.Attach();
        for (int i = 0; i < 16; i++)
            system.Tick(new[] { ReproWorlds.Player(new Vector3(12f, 0f, 12f)) }, ReproWorlds.TickRate);

        ReproDump dump = ReproCapture.Build(
            ReproWorlds.Request(new ReproWorlds.Geometry().Ground(), new Vector3(12f, 0f, 12f)),
            system, recorder, ReproWorlds.FlatGround);

        var scenario = new ReproScenario(dump);
        Assert.Null(scenario.System.MoveResolver);
        Assert.Null(scenario.System.GroundSnap);
        Assert.Null(scenario.System.VisionBlocked);
        Assert.Null(scenario.System.PhysicalLineBlocked);
        ReproReplayReport report = scenario.Run();
        Assert.True(report.ReproducesRecording, report.Describe());
        Assert.Equal(0, report.UnansweredInWindow);
    }

    // A query outside the captured radius is answered by an empty world, which is indistinguishable
    // from open ground. The replay still moves the body — freezing would be worse — but books the
    // answer as one nobody could give.
    [Fact]
    public void QueriesOutsideTheCapturedSliceAreNotEvidence()
    {
        ReproTriangles triangles = ReproTriangles.From(
            new ReproWorlds.Geometry().Ground().Build(Vector3.Zero, 8f))!;
        var world = new ReproCollisionWorld(triangles, null, Vector3.Zero, 8f);
        Assert.True(world.Covers(new Vector3(4f, 0f, 4f)));
        Assert.False(world.Covers(new Vector3(40f, 0f, 40f)));

        var dump = new ReproDump
        {
            World = new ReproWorldData
            {
                Bounds = { Box() },
                Tables = { new ReproZombieTable { Name = "Test", Damage = 5 } },
                Geometry = new ReproWorlds.Geometry().Ground().Build(new Vector3(500f, 0f, 500f), 4f),
            },
            Zombies = new ReproZombieSection
            {
                Seams = new ReproWorldSeams { MoveResolver = true, GroundSnap = true },
                State = new ReproSystemState
                {
                    Zombies =
                    {
                        new ReproZombieRecord
                        {
                            Id = 1,
                            Position = new[] { 20f, 0f, 20f },
                            State = EZombieState.Chase,
                            Target = 1,
                        },
                    },
                },
                Frames =
                {
                    new ReproFrame
                    {
                        Dt = 0.08f,
                        Players =
                        {
                            new ReproPlayerSample
                            {
                                Id = 1,
                                Position = new[] { 30f, 0f, 30f },
                                Stance = UnturnedGodot.Player.EPlayerStance.Sprint,
                            },
                        },
                    },
                },
            },
        };

        // The incident is 500 m from the geometry the dump carries, so nothing it asks is covered.
        ReproReplayReport report = new ReproScenario(dump).Run(extraTicks: 4);
        Assert.True(report.UnansweredQueries > 0);
        Assert.Equal(0, report.GeometryAnswers);
    }

    // The recording says these zombies stayed asleep. That is a claim to check, not an absence: a
    // change that newly alerts one has no position to disagree with unless the silence is verified.
    [Fact]
    public void AZombieTheRecordingSaysStayedIdleMustStayIdle()
    {
        var dump = new ReproDump
        {
            World = new ReproWorldData
            {
                Bounds = { Box() },
                Tables = { new ReproZombieTable { Name = "Test", Damage = 5 } },
            },
            Zombies = new ReproZombieSection
            {
                Seams = new ReproWorldSeams(),
                State = new ReproSystemState
                {
                    Zombies =
                    {
                        new ReproZombieRecord { Id = 1, Position = new[] { 20f, 0f, 20f } },
                        new ReproZombieRecord
                        {
                            Id = 2,
                            Position = new[] { 21f, 0f, 21f },
                            State = EZombieState.Chase,
                            Target = 3,
                        },
                    },
                },
                // Only the active one was sampled; the idle one's absence is the claim.
                Frames =
                {
                    new ReproFrame
                    {
                        Dt = 0.08f,
                        Zombies =
                        {
                            new ReproMotionSample
                            {
                                Id = 2,
                                Position = new[] { 21f, 0f, 21f },
                                State = EZombieState.Chase,
                                Target = 3,
                            },
                        },
                    },
                },
            },
        };

        // Replayed as recorded, the idle one stays idle and the window reproduces.
        Assert.Equal(0, new ReproScenario(dump).Run().SilentWakeups);

        // Woken by hand — standing in for a change that newly alerts it — the verdict notices, even
        // though it contributes no position of its own to compare.
        var scenario = new ReproScenario(dump);
        foreach (ZombieInstance zombie in scenario.System.Zombies)
            if (zombie.Id == 1)
            {
                zombie.State = EZombieState.Return; // walking somewhere it was not recorded walking
                zombie.LeaveTo = new Vector3(60f, 0f, 60f);
            }
        ReproReplayReport report = scenario.Run();
        Assert.True(report.SilentWakeups > 0);
        Assert.False(report.ReproducesRecording);
        Assert.Contains("woke up unexpectedly", report.Describe(), StringComparison.Ordinal);
    }

    // The slice is the world the replay has. A body whose CENTRE is inside it can still be leaning on
    // a wall whose triangles are outside, so coverage has to account for how far the query reaches.
    [Fact]
    public void CoverageAccountsForTheBodyNotJustItsCentre()
    {
        ReproTriangles triangles = ReproTriangles.From(
            new ReproWorlds.Geometry().Ground().Build(Vector3.Zero, 16f))!;
        var world = new ReproCollisionWorld(triangles, null, Vector3.Zero, 16f);

        Assert.True(world.Covers(new Vector3(10f, 0f, 0f)));
        // ...and the same point stops being covered once the question reaches past the edge.
        Assert.True(world.Covers(new Vector3(10f, 0f, 0f), 5f));
        Assert.False(world.Covers(new Vector3(10f, 0f, 0f), 7f));

        // A slice with no radius at all (a hand-written dump) covers everything rather than nothing.
        var unbounded = new ReproCollisionWorld(triangles);
        Assert.True(unbounded.Covers(new Vector3(5000f, 0f, 5000f), 100f));
    }

    // A zombie that was mid-something when the window opened and settles on the very first tick is
    // the whole incident in a short recording. Measuring it only from the tick after it settles
    // measures nothing at all.
    [Fact]
    public void AZombieThatSettlesOnTheFirstTickIsStillMeasured()
    {
        var dump = new ReproDump
        {
            World = new ReproWorldData
            {
                Bounds = { Box() },
                Tables = { new ReproZombieTable { Name = "Test", Damage = 5 } },
            },
            Zombies = new ReproZombieSection
            {
                Seams = new ReproWorldSeams(),
                State = new ReproSystemState
                {
                    Zombies =
                    {
                        // On its retreat point already: the first Step arrives and it stops.
                        new ReproZombieRecord
                        {
                            Id = 4,
                            Position = new[] { 20f, 0f, 20f },
                            State = EZombieState.Return,
                            Target = byte.MaxValue,
                            LeaveTo = new[] { 20.2f, 0f, 20f },
                        },
                    },
                },
                Frames = { new ReproFrame { Dt = 0.08f } },
            },
        };

        ReproReplayReport report = new ReproScenario(dump).Run();
        ReproMotionSummary summary = Assert.Single(report.Motion);
        Assert.Equal(4, summary.Id);
        Assert.Equal(EZombieState.Idle, summary.FinalState);
    }

    // The dump's navmesh slice reaches as far as the capture did. A route asked for beyond it comes
    // back "no route" from a graph that never had that ground — which is not the same claim as a map
    // with no way through, and must not read as one.
    [Fact]
    public void RoutesBeyondTheNavmeshSliceAreNotEvidence()
    {
        NavFlag field = ReproWorlds.FlatField();
        var focus = new Vector3(6f, 0f, 6f);
        var dump = new ReproDump
        {
            Meta = new ReproMeta { FocusPoint = ReproVector.From(focus), FocusRadius = 6f },
            World = new ReproWorldData
            {
                HasNavmesh = true,
                Bounds = { Box() },
                Tables = { new ReproZombieTable { Name = "Test", Damage = 5 } },
                NavFlags = { ReproCapture.Slice(field, focus, 6f) },
            },
            Zombies = new ReproZombieSection
            {
                Seams = new ReproWorldSeams { PathQuery = true },
                State = new ReproSystemState
                {
                    Zombies =
                    {
                        new ReproZombieRecord
                        {
                            Id = 1,
                            Position = new[] { 6f, 0f, 6f },
                            State = EZombieState.Chase,
                            Target = 1,
                        },
                    },
                },
                Frames =
                {
                    new ReproFrame
                    {
                        Dt = 0.08f,
                        Players =
                        {
                            // Well outside the slice: every route asked for is one the dump cannot
                            // answer, however confidently the graph says "no".
                            new ReproPlayerSample
                            {
                                Id = 1,
                                Position = new[] { 35f, 0f, 35f },
                                Stance = UnturnedGodot.Player.EPlayerStance.Sprint,
                            },
                        },
                    },
                },
            },
        };

        ReproReplayReport sliced = new ReproScenario(dump).Run(extraTicks: 8);
        Assert.True(sliced.RoutesRecomputed > 0);
        Assert.True(sliced.UnansweredQueries > 0);

        // Handed the whole map, the same routes are answerable and stop being counted against it.
        ReproReplayReport whole = new ReproScenario(dump, new ReproScenarioOptions
        {
            NavFlags = new[] { field },
        }).Run(extraTicks: 8);
        Assert.True(whole.RoutesRecomputed > 0);
        Assert.Equal(0, whole.UnansweredQueries);
    }

    [Fact]
    public void PortalProbesOutsideTheCollisionSliceAreNotEvidenceOfAnOpenSeam()
    {
        static NavFlag TJunction() => new()
        {
            Center = new Vector3(2f, 0f, 1f),
            Size = new Vector3(6f, 10f, 4f),
            Vertices =
            [
                new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 2f),
                new Vector3(2f, 0f, 0f), new Vector3(2f, 0f, 2f),
                new Vector3(2f, 0f, 1f), new Vector3(4f, 0f, 0f),
                new Vector3(4f, 0f, 1f), new Vector3(4f, 0f, 2f),
            ],
            Triangles = [0, 1, 2, 1, 3, 2, 2, 4, 5, 4, 6, 5, 4, 3, 6, 3, 7, 6],
        };

        var dump = new ReproDump
        {
            World = new ReproWorldData
            {
                HasNavmesh = true,
                Bounds = { Box() },
                Geometry = new ReproWorlds.Geometry().Ground()
                    .Build(new Vector3(100f, 0f, 100f), 4f),
            },
            Zombies = new ReproZombieSection
            {
                Seams = new ReproWorldSeams { PathQuery = true },
                Frames = { new ReproFrame { Dt = 0.08f } },
            },
        };
        var scenario = new ReproScenario(dump, new ReproScenarioOptions
        {
            RecomputePaths = true,
            NavFlags = new[] { TJunction() },
        });
        var path = new List<Vector3>();

        Assert.True(scenario.System.PathQuery!(new Vector3(0.5f, 0f, 1f),
            new Vector3(3.5f, 0f, 1f), path, BakedNavGraph.AgentRadius));
        Assert.True(scenario.UnansweredQueries > 0,
            "the out-of-slice stitched portal was reported as covered geometry");
        Assert.Equal(0, scenario.UnansweredInWindow); // direct diagnostic call occurs outside Step
    }
}
