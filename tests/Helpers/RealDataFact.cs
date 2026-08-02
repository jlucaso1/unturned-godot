using Xunit;

namespace UnturnedGodot.Tests.Helpers;

// Marks a test that needs the real game content, so a machine without it reports a SKIP rather than a PASS.
//
// The suite used to guard these with `if (path == null) return;`, which xUnit cannot tell from a test that
// ran and asserted. The numbers said so: with UNTURNED_PATH unset the run reported the same 1170 passed and
// 0 skipped as a run with the content present — in a tenth of the time, because a whole class of tests was
// asserting nothing while counting as green.
//
// xUnit v2 has no runtime Assert.Skip (that is v3), so the decision is made where v2 can act on it: in the
// attribute, before the test is collected.
//
// UG_REQUIRE_REAL_DATA=1 turns the skip off. A job that fetched the content on purpose wants a missing file
// to fail rather than quietly vanish from the run — the point of that job is to prove these ran.
public class RealDataFactAttribute : FactAttribute
{
    // A map that must be present under Maps/, e.g. "PEI". Null means any install will do.
    public string? Map { get; init; }

    // Whether the platform's core masterbundle has to be resolvable inside the install.
    public bool RequiresMasterBundle { get; init; }

    public override string? Skip
    {
        get => base.Skip ?? RealData.SkipReason(Map, RequiresMasterBundle);
        set => base.Skip = value;
    }
}

// The [Theory] counterpart; same rules.
public class RealDataTheoryAttribute : TheoryAttribute
{
    public string? Map { get; init; }
    public bool RequiresMasterBundle { get; init; }

    public override string? Skip
    {
        get => base.Skip ?? RealData.SkipReason(Map, RequiresMasterBundle);
        set => base.Skip = value;
    }
}

public static class RealData
{
    // Set by the workflow that fetches the content, so a skip there is a failure instead.
    public static bool Required { get; } =
        System.Environment.GetEnvironmentVariable("UG_REQUIRE_REAL_DATA") == "1";

    // Null when the test can run; otherwise why it cannot.
    public static string? SkipReason(string? map, bool requiresMasterBundle)
    {
        if (Required)
            return null; // let it run and fail loudly: this job exists to prove these tests execute

        if (GameData.Install == null)
            return "no Unturned install found; set UNTURNED_PATH or run ./scripts/fetch-game-data.sh";
        if (map != null && GameData.Map(map) == null)
            return $"the install has no {map} map";
        if (requiresMasterBundle && GameData.MasterBundle == null)
            return "the install has no core masterbundle for this platform";
        return null;
    }
}
