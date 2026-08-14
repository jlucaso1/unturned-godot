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

    // The maps UG_REQUIRE_REAL_DATA actually vouches for.
    //
    // It can only promise what the fetch pulled, and fetch-game-data.sh pulls PEI unless told otherwise
    // — so that is the default here, matching the script rather than restating a number. A job that runs
    // `--maps all` (or any other list) sets UG_REAL_DATA_MAPS to the same list and gets the same loud
    // failure for those maps.
    //
    // Without this, `Required` forced EVERY [RealDataFact] to run, including one asking for a map the
    // fetch never downloads. That is not a silent skip — the thing this attribute exists to catch — it
    // is content nobody promised, and the test died on a null path inside the job that is supposed to
    // prove these tests pass. Only Russia ships a safezone, so the test for it is exactly that case.
    private static readonly string[] GuaranteedMaps =
        (System.Environment.GetEnvironmentVariable("UG_REAL_DATA_MAPS") is { Length: > 0 } list
            ? list
            : "PEI").Split(',', System.StringSplitOptions.RemoveEmptyEntries
                | System.StringSplitOptions.TrimEntries);

    private static bool IsGuaranteed(string map) =>
        System.Array.Exists(GuaranteedMaps,
            m => m.Equals("all", System.StringComparison.OrdinalIgnoreCase)
                || m.Equals(map, System.StringComparison.OrdinalIgnoreCase));

    // Null when the test can run; otherwise why it cannot.
    public static string? SkipReason(string? map, bool requiresMasterBundle)
    {
        // Checked BEFORE Required: a map outside the fetch is absent by arrangement, not by accident,
        // and forcing the test to run would only turn a missing file into a null-reference stack trace.
        if (map != null && !IsGuaranteed(map) && GameData.Map(map) == null)
            return $"the install has no {map} map, and the content fetch does not include it";

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
