using System;
using System.IO;
using UnturnedGodot.Repro;
using Xunit;

namespace UnturnedGodot.Tests.Repro;

// docs/REPRO.md's worked example, landed.
//
// The doc has always shown a dump becoming a regression test in five lines, and the repo had no
// instance of it. ReproRoundTripTests builds its world from scratch instead — correctly, because it is
// testing the capture/replay round trip and a fixture would only prove the fixture still parses. But
// "hand this file to whoever is fixing it and they can run the same five seconds as many times as they
// like" is the whole promise of the harness, and nothing here exercised the FILE half of it: reading a
// dump somebody else produced, on a machine that has never seen the session that made it.
//
// That gap has a cost beyond documentation. ReproDump.Read is the only entry point a bug report ever
// uses, and until now nothing read a dump this process did not just write — so a schema change that
// broke deserialising a dump from disk would have been caught by no test in the suite.
//
// WHY THIS DUMP CAN LIVE IN THE REPOSITORY
//
// Real dumps carry a slice of the map's own navmesh and collision, which is game content, so they
// belong in an issue rather than here (see NOTICE.md, and the same note in docs/REPRO.md). This one is
// synthetic: a flat ground plane and one box called "pole", on a level named TestField, produced by
// tests/Repro/ReproWorlds.cs. There is not a byte of Unturned in it. That is what makes a committed
// worked example possible at all, and it is the shape anyone adding another fixture here has to keep.
//
// REGENERATING IT
//
// It is a generated artifact, not a hand-edited one: 40 ticks of ReproWorlds' hunt-past-a-pole session,
// captured through ReproCapture.Build and written with ReproDump.ToJson(). Compact rather than pretty
// on purpose — 75 KB against 297 KB, and nobody diffs it line by line because a change to it is always
// a wholesale re-record. If the schema moves and this has to be rebuilt, rebuild it from that session
// rather than editing the JSON.
public class ReproFixtureDumpTests
{
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "repro-hunt-past-a-pole.json");

    private static ReproDump Load()
    {
        Assert.True(File.Exists(FixturePath),
            $"the committed repro fixture is missing at {FixturePath}. It is copied to the output by "
            + "the csproj's Fixtures/** item; if that item moved, this test cannot run.");
        return ReproDump.Read(FixturePath);
    }

    // The five lines docs/REPRO.md advertises, run against a dump read off disk. If this stops
    // compiling, the doc is wrong and needs the same edit.
    [Fact]
    public void ADumpOnDiskBecomesARegressionTest()
    {
        ReproDump dump = ReproDump.Read(FixturePath);
        ReproReplayReport report = new ReproScenario(dump).Run(extraTicks: 200);
        ReproMotionSummary hunter = report.Motion[0];

        Assert.False(hunter.IsSpinningInPlace);
        Assert.True(hunter.NetDisplacementMetres > 5f,
            $"the hunter covered {hunter.NetDisplacementMetres:F2} m; it is supposed to reach the player");
    }

    // The measurement the example rests on, pinned loosely. Loosely because the numbers are the
    // simulation's, not this test's: pinning them exactly would make any deliberate change to the
    // zombie brain fail here first, with a message about a fixture rather than about the brain — which
    // is the failure mode this whole branch exists to remove. What is pinned is the SHAPE: it hunts,
    // it arrives, and it does not spin.
    [Fact]
    public void TheRecordedWindowStillReplaysExactly()
    {
        ReproDump dump = Load();
        ReproReplayReport report = new ReproScenario(dump).Run();

        Assert.True(report.ComparedSamples > 0, "the fixture recorded no motion to compare against");
        Assert.Equal(0, report.UnansweredQueries);
        Assert.True(report.ReproducesRecording,
            $"the committed dump no longer replays against this build:\n{report.Describe()}");
    }

    // The other half of what a dump is for, and the half that keeps working once somebody CHANGES the
    // brain: with the recorded answers switched off, the geometry slice has to answer instead. A dump
    // whose triangles were decoration would pass the test above and fail this one.
    [Fact]
    public void TheGeometrySliceAnswersWithTheRecordingSwitchedOff()
    {
        ReproDump dump = Load();
        var scenario = new ReproScenario(dump, new ReproScenarioOptions { UseRecordedAnswers = false });
        ReproReplayReport report = scenario.Run();

        Assert.Equal(0, report.OracleHits);
        Assert.True(report.GeometryAnswers > 0, "the dump's geometry answered nothing");
    }

    // The reason this file may be committed at all. A fixture that quietly grew a slice of a real map
    // would be a licensing problem rather than a test failure, so it is asserted rather than trusted:
    // the level is the synthetic one, and the collision it carries names only shapes ReproWorlds built.
    [Fact]
    public void TheFixtureCarriesNoGameContent()
    {
        ReproDump dump = Load();

        Assert.Equal("TestField", dump.Meta.LevelName);
        Assert.Equal("TestField", dump.Meta.Map);

        // ReproWorlds builds exactly two things: the ground plane and one box named "pole". Every
        // collision triangle in a dump names the object it belongs to, so this is the whole of what the
        // slice could have come from.
        Assert.NotNull(dump.World.Geometry);
        foreach (string name in dump.World.Geometry!.OwnerNames)
        {
            Assert.True(name is "ground" or "pole",
                $"the fixture carries collision named '{name}', which ReproWorlds did not build — if "
                + "this dump was re-recorded from a real map it carries game content and cannot be "
                + "committed (see NOTICE.md).");
        }
    }
}
