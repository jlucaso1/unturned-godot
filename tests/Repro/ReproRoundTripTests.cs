using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Repro;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Repro;

// The end-to-end claim the whole harness rests on: run a session, capture it, replay the capture, and
// get the same simulation back. Every other test here pins one piece; these pin the piece fitting
// together, which is the only thing a bug report actually needs.
public class ReproRoundTripTests
{
    // A hunt past an obstacle: the zombie aggros, routes around a pole and keeps coming. Enough moving
    // parts (detection, pathfinding, collision, ground) that a harness that drops any one of them
    // shows up as a divergence.
    private static (ReproDump Dump, List<Vector3> Live) Record(int ticks = 40, ulong seed = 1)
    {
        var geometry = new ReproWorlds.Geometry().Ground()
            .Box("pole", new Vector3(12f, 1.5f, 12f), new Vector3(0.25f, 1.5f, 0.25f));
        (ZombieSystem system, _, _) = ReproWorlds.Session(geometry, seed);
        var recorder = new ReproRecorder(system, new ReproRecorderOptions { WindowTicks = 64 });
        recorder.Attach();

        var live = new List<Vector3>();
        ZombieInstance zombie = system.Zombies[0];
        for (int i = 0; i < ticks; i++)
        {
            var player = new Vector3(16f + (i * 0.1f), 0f, 16f);
            system.AuthoritativeTick = (uint)(100 + i);
            system.Tick(new[] { ReproWorlds.Player(player) }, ReproWorlds.TickRate);
            live.Add(zombie.Position);
        }

        ReproDump dump = ReproCapture.Build(
            ReproWorlds.Request(geometry, new Vector3(16f, 0f, 16f), "spinning at the pole"),
            system, recorder, ReproWorlds.FlatGround);
        recorder.Detach();
        return (dump, live);
    }

    [Fact]
    public void ARecordedSessionReplaysExactly()
    {
        (ReproDump dump, _) = Record();
        Assert.NotNull(dump.Zombies);
        Assert.NotEmpty(dump.Zombies!.Frames);

        var scenario = new ReproScenario(dump);
        ReproReplayReport report = scenario.Run();

        Assert.True(report.ComparedSamples > 0, "the window recorded no motion to compare against");
        Assert.True(report.ReproducesRecording,
            $"replay diverged from the recording:\n{report.Describe()}");
        Assert.Equal(0, report.UnansweredQueries);
    }

    // The same window with the recording switched off: every world query is answered from the dump's
    // triangles instead. This is the case that matters once someone starts CHANGING the brain, and it
    // has to land in the same place — otherwise the geometry in a dump is decoration.
    [Fact]
    public void TheGeometrySliceAloneReproducesTheSameRun()
    {
        (ReproDump dump, _) = Record();
        var scenario = new ReproScenario(dump, new ReproScenarioOptions { UseRecordedAnswers = false });
        ReproReplayReport report = scenario.Run();

        Assert.Equal(0, report.OracleHits);
        Assert.True(report.GeometryAnswers > 0);
        Assert.True(report.ReproducesRecording,
            $"the dump's geometry did not reproduce the session:\n{report.Describe()}");
    }

    // Running past the recording with no geometry to fall back on leaves queries nobody can answer.
    // The replay counts them instead of quietly treating the world as empty, because a run with a
    // high count there is not evidence of anything.
    [Fact]
    public void QueriesNothingCanAnswerAreCounted()
    {
        (ReproDump dump, _) = Record();
        var scenario = new ReproScenario(dump, new ReproScenarioOptions { UseGeometry = false });
        ReproReplayReport report = scenario.Run(extraTicks: 20);
        Assert.True(report.OracleMisses > 0);
        Assert.True(report.UnansweredQueries > 0);

        // UseGeometry switches off the COLLISION world; the dump's navmesh slice is still built and
        // still routes near the incident, so a route asked for INSIDE that slice is booked as a
        // geometry answer. This used to assert an exact zero, which held only because no route in this
        // window happened to fall inside the slice — an accident of which spawn points the roll picked,
        // not a property of the option. What the option does guarantee is that geometry cannot carry
        // the run: the queries nobody can answer still dominate.
        Assert.True(report.UnansweredQueries > report.GeometryAnswers,
            $"geometry answered more than it could not:\n{report.Describe()}");
    }

    [Fact]
    public void TheDumpSurvivesJsonAndGzip()
    {
        (ReproDump dump, _) = Record();
        ReproDump? parsed = ReproDump.FromJson(dump.ToJson());
        Assert.NotNull(parsed);
        ReproReplayReport report = new ReproScenario(parsed!).Run();
        Assert.True(report.ReproducesRecording, report.Describe());

        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "repro-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            string plain = System.IO.Path.Combine(directory, "dump.json");
            string zipped = System.IO.Path.Combine(directory, "dump.json.gz");
            dump.Write(plain);
            dump.Write(zipped);
            Assert.True(new System.IO.FileInfo(zipped).Length
                < new System.IO.FileInfo(plain).Length);
            Assert.True(new ReproScenario(ReproDump.Read(zipped)).Run().ReproducesRecording);
            Assert.True(new ReproScenario(ReproDump.Read(plain)).Run().ReproducesRecording);
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    // A dump is a bug report, not a level export. Nothing here is a hard budget, but a window that
    // silently grows into the tens of megabytes stops being something anyone will attach to an issue.
    [Fact]
    public void TheWindowStaysSmall()
    {
        (ReproDump dump, _) = Record(ticks: 64);
        int bytes = dump.ToJson().Length;
        Assert.True(bytes < 2_000_000,
            $"a 64-tick window over a flat field serialized to {bytes / 1024} KB");
    }

    // Replaying twice from one dump has to give the same answer both times: the recorded answers are
    // handed out per tick, and a cursor that did not rewind would starve the second run.
    [Fact]
    public void ADumpCanBeReplayedMoreThanOnce()
    {
        (ReproDump dump, _) = Record();
        ReproReplayReport first = new ReproScenario(dump).Run();
        ReproReplayReport second = new ReproScenario(dump).Run();
        Assert.Equal(first.MaxPositionError, second.MaxPositionError);
        Assert.Equal(first.OracleHits, second.OracleHits);
    }

    // Running past the recorded window is how a fix is judged: the window shows the bug, the tail says
    // whether it ends. The player is held where the recording left them.
    [Fact]
    public void ItKeepsGoingPastTheWindow()
    {
        (ReproDump dump, _) = Record();
        var scenario = new ReproScenario(dump);
        ReproReplayReport report = scenario.Run(extraTicks: 60);
        Assert.Equal(dump.Zombies!.Frames.Count + 60, report.Ticks);
        Assert.NotEmpty(report.Motion);

        // The zombie was hunting a player it can reach over open ground, so by the end of the tail it
        // has closed on them rather than milling about — the property a fix for a stuck zombie has to
        // restore, expressed the way the report expresses it.
        ReproMotionSummary hunter = report.Motion[0];
        Assert.False(hunter.IsSpinningInPlace, report.Describe());
        Assert.True(hunter.NetDisplacementMetres > 5f, report.Describe());
    }

    [Fact]
    public void TheDumpDescribesItself()
    {
        (ReproDump dump, _) = Record();
        string description = dump.Describe();
        Assert.Contains("TestField", description, StringComparison.Ordinal);
        Assert.Contains("spinning at the pole", description, StringComparison.Ordinal);
        Assert.Contains("collision triangles", description, StringComparison.Ordinal);
        Assert.Contains("hunting", description, StringComparison.Ordinal);
    }
}
