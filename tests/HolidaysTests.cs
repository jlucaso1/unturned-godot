using System;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

// HolidayUtil's schedule, over the dates the game itself hardcodes. Every case here is a wall-clock
// local time, because that is what Unturned builds its windows out of before converting them to UTC.
public class HolidayUtilTests
{
    private static ENPCHoliday On(int year, int month, int day, int hour = 12, int minute = 0, int second = 0)
        => HolidayUtil.GetScheduledHoliday(new DateTime(year, month, day, hour, minute, second));

    [Theory]
    // Halloween: October 20th through noon on November 1st.
    [InlineData(2024, 10, 25, (int)ENPCHoliday.Halloween)]
    [InlineData(2024, 10, 19, (int)ENPCHoliday.None)]
    // Christmas: December 7th through noon on January 2nd.
    [InlineData(2024, 12, 25, (int)ENPCHoliday.Christmas)]
    [InlineData(2024, 12, 6, (int)ENPCHoliday.None)]
    [InlineData(2024, 4, 1, (int)ENPCHoliday.AprilFools)]
    [InlineData(2024, 2, 14, (int)ENPCHoliday.Valentines)]
    [InlineData(2024, 2, 15, (int)ENPCHoliday.None)]
    [InlineData(2024, 6, 15, (int)ENPCHoliday.PrideMonth)]
    [InlineData(2024, 7, 7, (int)ENPCHoliday.UnturnedAnniversary)]
    // The date this finding was measured on, and the whole point of it: 285 of PEI's placements were
    // being drawn here.
    [InlineData(2026, 8, 14, (int)ENPCHoliday.None)]
    public void GetScheduledHoliday_MatchesTheGamesCalendar(int year, int month, int day, int expected) =>
        Assert.Equal((ENPCHoliday)expected, On(year, month, day));

    [Fact]
    public void GetScheduledHoliday_ChristmasStraddlesTheNewYear()
    {
        // The one window that crosses a year boundary, and the reason scheduleHolidays anchors it to the
        // December it started in rather than to the current year: past June the December ahead is this
        // year's, and before July the window that could still be open is last year's.
        Assert.Equal(ENPCHoliday.Christmas, On(2024, 12, 31, hour: 23));
        Assert.Equal(ENPCHoliday.Christmas, On(2025, 1, 1));
        Assert.Equal(ENPCHoliday.Christmas, On(2025, 1, 2, hour: 11, minute: 59));
        // Ends at noon on the 2nd, not at midnight.
        Assert.Equal(ENPCHoliday.None, On(2025, 1, 2, hour: 12, minute: 0, second: 1));
        // Either side of the June/July anchor flip, where the window being considered swaps from last
        // year's December to this year's: neither day may come back Christmas.
        Assert.Equal(ENPCHoliday.PrideMonth, On(2025, 6, 30));
        Assert.Equal(ENPCHoliday.None, On(2025, 7, 1));
    }

    [Fact]
    public void GetScheduledHoliday_IsInclusiveAtBothEnds()
    {
        // DateTimeRange.isWithinRange compares with >= and <=, so the first and last instants count.
        Assert.Equal(ENPCHoliday.Halloween, On(2024, 10, 20, hour: 0, minute: 0, second: 0));
        Assert.Equal(ENPCHoliday.Halloween, On(2024, 11, 1, hour: 12, minute: 0, second: 0));
        Assert.Equal(ENPCHoliday.None, On(2024, 11, 1, hour: 12, minute: 0, second: 1));
        Assert.Equal(ENPCHoliday.PrideMonth, On(2024, 6, 30, hour: 23, minute: 59, second: 59));
    }

    [Fact]
    public void GetScheduledHoliday_LeavesLunarNewYearUnscheduled()
    {
        // A documented divergence, not an oversight: Unturned schedules Lunar New Year from the backend's
        // HolidayStatusData (an override range, or a lunisolar date plus LunarNewYear_Days), and none of
        // those three values exists in any file an install carries. Inventing a window would put the
        // port's holiday on days the game's is not. February 10th 2024 was the lunar new year.
        Assert.Equal(ENPCHoliday.None, On(2024, 2, 10));
        // The override still reaches it, which is what makes the dressing reproducible.
        Assert.Equal(ENPCHoliday.LunarNewYear, HolidayUtil.ParseOverride("LNY"));
    }

    [Theory]
    [InlineData("Halloween", (int)ENPCHoliday.Halloween)]
    [InlineData("hw", (int)ENPCHoliday.Halloween)]
    [InlineData("Christmas", (int)ENPCHoliday.Christmas)]
    [InlineData("XMAS", (int)ENPCHoliday.Christmas)]
    [InlineData("aprilfools", (int)ENPCHoliday.AprilFools)]
    [InlineData("Valentines", (int)ENPCHoliday.Valentines)]
    [InlineData("PrideMonth", (int)ENPCHoliday.PrideMonth)]
    [InlineData("LunarNewYear", (int)ENPCHoliday.LunarNewYear)]
    [InlineData("UnturnedAnniversary", (int)ENPCHoliday.UnturnedAnniversary)]
    // The one spelling Unturned's own switch has no way to say, and the reason for the nullable.
    [InlineData("None", (int)ENPCHoliday.None)]
    [InlineData("off", (int)ENPCHoliday.None)]
    public void ParseOverride_AcceptsTheSpellingsTheGamesSwitchDoes(string value, int expected) =>
        Assert.Equal((ENPCHoliday)expected, HolidayUtil.ParseOverride(value));

    [Theory]
    // A closed set, like EnvFlag's: anything else is a typo, and "nobody asked" is null rather than
    // None, so a typo still consults the calendar instead of silently pinning the world to no holiday.
    [InlineData("Easter")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ParseOverride_ReturnsNullWhenNobodyAsked(string? value) =>
        Assert.Null(HolidayUtil.ParseOverride(value));

    [Fact]
    public void ParseOverride_SeparatesNoOverrideFromAnOverrideOfNone()
    {
        // The distinction the whole change exists for. Both used to be ENPCHoliday.None, so "pin this
        // run to no holiday" was inexpressible and every caller fell through to the clock.
        Assert.Null(HolidayUtil.ParseOverride(null));
        Assert.Equal(ENPCHoliday.None, HolidayUtil.ParseOverride("None"));
    }

    [Fact]
    public void Resolve_PrefersTheOverrideOverTheCalendar()
    {
        var august = new DateTime(2026, 8, 14, 12, 0, 0);
        var christmasDay = new DateTime(2024, 12, 25, 12, 0, 0);

        Assert.Equal(ENPCHoliday.Christmas, HolidayUtil.Resolve("XMAS", august));
        Assert.Equal(ENPCHoliday.None, HolidayUtil.Resolve(null, august));
        Assert.Equal(ENPCHoliday.None, HolidayUtil.Resolve("nonsense", august));
        Assert.Equal(ENPCHoliday.Christmas, HolidayUtil.Resolve(null, christmasDay));
    }

    [Fact]
    public void Resolve_PinsToNoHolidayEvenOnChristmasDay()
    {
        // What the structural-metrics gate runs with. Without it the recorded placement counts are only
        // true for the ~46 weeks a year nothing is running, and the job goes red every December having
        // found nothing wrong.
        var christmasDay = new DateTime(2024, 12, 25, 12, 0, 0);

        Assert.Equal(ENPCHoliday.None, HolidayUtil.Resolve("None", christmasDay));
        Assert.Equal(ENPCHoliday.None, HolidayUtil.Resolve("Off", christmasDay));
        // And the calendar still wins when nothing is pinned, which is what makes the pin meaningful.
        Assert.Equal(ENPCHoliday.Christmas, HolidayUtil.Resolve(null, christmasDay));
    }

    [Fact]
    public void IsHolidayActive_NeverMatchesAnUnrestrictedAsset()
    {
        // NONE comparing equal to itself would gate every object in the game on there being no holiday.
        Assert.False(HolidayUtil.IsHolidayActive(ENPCHoliday.None, ENPCHoliday.None));
        Assert.True(HolidayUtil.IsHolidayActive(ENPCHoliday.Christmas, ENPCHoliday.Christmas));
        Assert.False(HolidayUtil.IsHolidayActive(ENPCHoliday.Christmas, ENPCHoliday.Halloween));
        Assert.False(HolidayUtil.IsHolidayActive(ENPCHoliday.Christmas, ENPCHoliday.None));
    }

    [Fact]
    public void ActiveHoliday_AgreesWithTheScheduleUnlessOverridden()
    {
        // Read once per process, so this is whatever the suite is running on; it must at least be the
        // answer the pure function gives for the same moment when nothing overrides it.
        if (Environment.GetEnvironmentVariable(HolidayUtil.OverrideEnvironmentVariable) == null)
            Assert.Equal(HolidayUtil.GetScheduledHoliday(DateTime.Now), HolidayUtil.ActiveHoliday);
    }
}

public class HolidayPolicyTests
{
    [Fact]
    public void AllowRedirects_NeedsAHolidayToBeRunning()
    {
        // shouldUseHolidayRedirects (Level.cs:282) is the AND of three things; the struct folds the
        // "a holiday is running" half in so nothing downstream has to remember to check both.
        Assert.False(new HolidayPolicy(ENPCHoliday.None, allowRedirects: true).AllowRedirects);
        Assert.True(new HolidayPolicy(ENPCHoliday.Christmas, allowRedirects: true).AllowRedirects);
        Assert.False(new HolidayPolicy(ENPCHoliday.Christmas, allowRedirects: false).AllowRedirects);
    }

    [Fact]
    public void None_IsOutOfSeasonWithNoSubstitutions()
    {
        Assert.Equal(ENPCHoliday.None, HolidayPolicy.None.Active);
        Assert.False(HolidayPolicy.None.AllowRedirects);
    }

    [Fact]
    public void FromClock_TakesTheHolidayButNotTheSubstitutions()
    {
        // No map means no Config.json, and a substitution without one would be an assumption. The
        // restriction gate is unconditional, so it is still carried.
        HolidayPolicy policy = HolidayPolicy.FromClock();

        Assert.Equal(HolidayUtil.ActiveHoliday, policy.Active);
        Assert.False(policy.AllowRedirects);
    }

    [Fact]
    public void ForMap_ReadsTheMapsOwnOptIn()
    {
        using var dir = new TempDir();
        dir.Write("Config.json", """{ "Allow_Holiday_Redirects": true }""");

        Assert.True(HolidayPolicy.ForMap(dir.Path, ENPCHoliday.Christmas).AllowRedirects);
        Assert.False(HolidayPolicy.ForMap(dir.Path, ENPCHoliday.None).AllowRedirects);
    }

    [Fact]
    public void ForMap_DefaultsToNoSubstitutionsWhenTheMapNeverAsked()
    {
        // Unturned deserializes onto a fresh LevelConfigData whose bools start false, so a map with no
        // config, or one that omits the key, opts out in the game too.
        using var dir = new TempDir();

        Assert.False(HolidayPolicy.ForMap(dir.Path, ENPCHoliday.Christmas).AllowRedirects);

        dir.Write("Config.json", """{ "Category": "Curated" }""");
        Assert.False(HolidayPolicy.ForMap(dir.Path, ENPCHoliday.Christmas).AllowRedirects);
    }

    [RealDataFact(Map = "PEI")]
    public void ForMap_PeiOptsIn()
    {
        // Verified against the shipped file: PEI's Config.json carries "Allow_Holiday_Redirects": true.
        Assert.True(UnturnedGodot.Data.MapCatalog.ReadAllowHolidayRedirects(GameData.Map("PEI")!));
    }
}
