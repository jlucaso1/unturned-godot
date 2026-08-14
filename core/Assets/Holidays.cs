using System;

namespace UnturnedGodot.Assets;

// ENPCHoliday (Unturned/Bundles/NPCHolidayCondition.cs), in its own declaration order.
//
// The order is load-bearing, not cosmetic: HolidayUtil.GetScheduledHoliday walks the enum from NONE+1
// upwards and returns the FIRST holiday whose window contains the moment, so two windows that overlap
// are resolved by declaration order rather than by which one opened first. Valentines sits ahead of
// Lunar New Year for exactly that reason — a lunar new year running across February 14 is still
// Valentines to the game.
public enum ENPCHoliday
{
    None = 0,
    Halloween,
    Christmas,
    AprilFools,
    Valentines,
    PrideMonth,
    LunarNewYear,
    UnturnedAnniversary,
}

// Ports HolidayUtil (Unturned/Utils/HolidayUtil.cs): which holiday, if any, the game considers active.
//
// It is not decoration. An object or tree asset carrying "Holiday_Restriction" does not exist outside
// its holiday — LevelObject.updateConditions and ResourceSpawnpoint both fold the answer here into
// areConditionsMet, which gates the GameObject (collision) and the renderers together. On PEI that is
// 254 Christmas objects, 31 Halloween ones and 82 Christmas trees, all of which this port used to draw,
// and let you walk into, in August.
//
// ONE DIVERGENCE, and it is worth stating plainly rather than papering over. Unturned's isHolidayActive
// reads `Provider.authorityHoliday` — the SERVER's answer, handed to each client on connect — so every
// machine in a session agrees about the date even when their clocks and time zones do not. This port
// has no holiday in its connection handshake, so each end resolves the calendar for itself. On one
// machine (singleplayer, a listen server) that is the same answer by construction. Across a dedicated
// server and a client in another time zone it can differ for a few hours either side of a holiday's
// edge, and because the placement list is what DamageableWorld indexes into, a disagreement there
// shifts those indices rather than merely changing what is drawn. Syncing it belongs with the netcode,
// not here; UG_HOLIDAY pins both ends meanwhile.
public static class HolidayUtil
{
    // Unturned's own -Holiday command-line override, which this port has no command line for; the env
    // var is the same switch under the repo's own convention, and it is what makes a screenshot of the
    // Christmas dressing reproducible in July.
    public const string OverrideEnvironmentVariable = "UG_HOLIDAY";

    // isHolidayActive(holiday): a restriction is met only by the holiday it names. NONE never matches a
    // real holiday, which is why LevelObject checks `holidayRestriction != NONE` before asking at all —
    // an unrestricted object must not be gated by an inactive NONE comparing equal to itself.
    public static bool IsHolidayActive(ENPCHoliday restriction, ENPCHoliday active) =>
        restriction != ENPCHoliday.None && restriction == active;

    // scheduleHolidays + GetScheduledHoliday, collapsed into one pass.
    //
    // Unturned builds each window from the LOCAL year and month, converts both ends to UTC, and compares
    // them against DateTime.UtcNow; the round-trip is kept here rather than comparing local-to-local,
    // because the two only agree away from a daylight-saving discontinuity and the point of a port is
    // not to decide which of those the game meant. `localNow` is local wall-clock time, so its Kind is
    // forced to Local before the conversion: an Unspecified value would otherwise be read as local by
    // ToUniversalTime while a Utc one would be read as already-converted, and the same instant would
    // then answer two different questions.
    public static ENPCHoliday GetScheduledHoliday(DateTime localNow)
    {
        DateTime utcNow = DateTime.SpecifyKind(localNow, DateTimeKind.Local).ToUniversalTime();
        int year = localNow.Year;

        // Christmas is the one window that straddles a new year, so it is anchored to the December it
        // started in: past June the current year's December is still ahead or in progress, and before
        // July the window that could still be open is last year's.
        int christmasStartYear = localNow.Month > 6 ? year : year - 1;

        // Walked in ENPCHoliday order, first match wins — see the enum's comment.
        if (IsWithin(utcNow, Local(year, 10, 20, 0, 0, 0), Local(year, 11, 1, 12, 0, 0)))
            return ENPCHoliday.Halloween;
        if (IsWithin(utcNow, Local(christmasStartYear, 12, 7, 0, 0, 0),
                Local(christmasStartYear + 1, 1, 2, 12, 0, 0)))
            return ENPCHoliday.Christmas;
        if (IsWithin(utcNow, Local(year, 4, 1, 0, 0, 0), Local(year, 4, 1, 23, 59, 59)))
            return ENPCHoliday.AprilFools;
        if (IsWithin(utcNow, Local(year, 2, 14, 0, 0, 0), Local(year, 2, 14, 23, 59, 59)))
            return ENPCHoliday.Valentines;
        if (IsWithin(utcNow, Local(year, 6, 1, 0, 0, 0), Local(year, 6, 30, 23, 59, 59)))
            return ENPCHoliday.PrideMonth;

        // LUNAR_NEW_YEAR is deliberately absent, and this is a divergence rather than an oversight.
        // Unturned schedules it from HolidayStatusData — LunarNewYear_StartOverride/_EndOverride, or
        // failing those a ChineseLunisolarCalendar date plus LunarNewYear_Days — and every one of those
        // three values arrives from the backend's Status.json, which no Unturned install carries on
        // disk (checked: nothing named Status.json ships in the client or the dedicated server). The
        // window length is not a constant that could be ported number for number, so guessing one would
        // put the port's lunar new year on days the game's is not. Nothing shipped is restricted to it
        // either: across every .dat in Bundles/, Holiday_Restriction only ever reads CHRISTMAS (106),
        // HALLOWEEN (7) or PRIDE_MONTH (1). UG_HOLIDAY=LunarNewYear still forces it.

        if (IsWithin(utcNow, Local(year, 7, 7, 0, 0, 0), Local(year, 7, 7, 23, 59, 59)))
            return ENPCHoliday.UnturnedAnniversary;

        return ENPCHoliday.None;
    }

    // The spellings Unturned's own -Holiday switch accepts, including its two abbreviations, plus one
    // this port adds. A closed set otherwise, like EnvFlag's: an unrecognised value leaves the schedule
    // alone rather than being guessed at, because a screenshot run silently falling back to "no holiday"
    // is a wasted run.
    //
    // Null is "nobody asked", ENPCHoliday.None is "asked for no holiday at all", and the two have to be
    // different values because they lead to different behaviour — the first consults the calendar and
    // the second overrides it. Unturned's own switch cannot say the second: `-Holiday` only ever names a
    // holiday to turn ON, and holidayOverride staying NONE is indistinguishable from the flag being
    // absent (HolidayUtil.cs's static constructor). This is a deliberate departure, and a small one, but
    // it is a departure and it is worth saying which way it runs.
    //
    // It earns its keep twice over. Reproducing a screenshot WITHOUT the tinsel on December 20th is
    // exactly as useful as forcing Christmas in July, and only one of those was expressible before. And
    // the structural-metrics gate — the one thing watching the render graph, which no unit test sees —
    // counts placed objects, so without a pin its recorded baseline silently becomes wrong for six weeks
    // of the year: Halloween adds 31 placements, Christmas 336, and June turns on a PRIDE_MONTH asset.
    // A deterministic gate that fails by the calendar is one people learn to ignore.
    public static ENPCHoliday? ParseOverride(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string trimmed = value.Trim();
        if (Is(trimmed, "None") || Is(trimmed, "Off"))
            return ENPCHoliday.None;
        if (Is(trimmed, "Halloween") || Is(trimmed, "HW"))
            return ENPCHoliday.Halloween;
        if (Is(trimmed, "Christmas") || Is(trimmed, "XMAS"))
            return ENPCHoliday.Christmas;
        if (Is(trimmed, "AprilFools"))
            return ENPCHoliday.AprilFools;
        if (Is(trimmed, "Valentines"))
            return ENPCHoliday.Valentines;
        if (Is(trimmed, "PrideMonth"))
            return ENPCHoliday.PrideMonth;
        if (Is(trimmed, "LunarNewYear") || Is(trimmed, "LNY"))
            return ENPCHoliday.LunarNewYear;
        if (Is(trimmed, "UnturnedAnniversary"))
            return ENPCHoliday.UnturnedAnniversary;
        return null;
    }

    // getActiveHoliday(): the override if one is set, otherwise the schedule. Read once per process like
    // Unturned's own static constructor, so a load that starts at 23:59:59 on October 31st cannot have
    // half its objects decide the holiday ended partway through.
    public static ENPCHoliday ActiveHoliday { get; } = Resolve(
        Environment.GetEnvironmentVariable(OverrideEnvironmentVariable), DateTime.Now);

    // Split out so the pair can be tested without the process's clock or environment. An override that
    // parsed — INCLUDING one that parsed as None — wins outright; only the absence of one falls through
    // to the calendar. That distinction is the whole point of ParseOverride returning a nullable.
    public static ENPCHoliday Resolve(string? overrideValue, DateTime localNow) =>
        ParseOverride(overrideValue) ?? GetScheduledHoliday(localNow);

    private static bool Is(string value, string name) =>
        string.Equals(value, name, StringComparison.OrdinalIgnoreCase);

    private static DateTime Local(int year, int month, int day, int hour, int minute, int second) =>
        new(year, month, day, hour, minute, second, DateTimeKind.Local);

    // DateTimeRange.isWithinRange: inclusive at both ends.
    private static bool IsWithin(DateTime utcNow, DateTime localStart, DateTime localEnd) =>
        utcNow >= localStart.ToUniversalTime() && utcNow <= localEnd.ToUniversalTime();
}

// What the holiday means for one map's placements, which is two separate answers that Unturned keeps
// apart and this port has to as well.
//
// `Active` gates Holiday_Restriction, and nothing turns it off: a Christmas prop is absent in August on
// every map, in the editor or out of it (LevelObject.cs:428, ResourceSpawnpoint.cs:539).
//
// `AllowRedirects` gates the *substitutions* — Christmas_Redirect / Halloween_Redirect — and Unturned
// requires three things at once for those (Level.cs:282): not the editor, the map's own
// Config.json saying Allow_Holiday_Redirects, and a holiday actually running. PEI says true; a map that
// says nothing gets no substitutions, which is why the flag is read rather than assumed.
public readonly struct HolidayPolicy
{
    public ENPCHoliday Active { get; }
    public bool AllowRedirects { get; }

    public HolidayPolicy(ENPCHoliday active, bool allowRedirects)
    {
        Active = active;
        // shouldUseHolidayRedirects folds "a holiday is running" into the flag itself, so nothing
        // downstream has to remember to check both.
        AllowRedirects = allowRedirects && active != ENPCHoliday.None;
    }

    // Out of season, and with substitutions off: what every caller wants for 47 weeks of the year, and
    // what a test wants when the clock is not the thing under test.
    public static HolidayPolicy None => new(ENPCHoliday.None, allowRedirects: false);

    // The holiday the clock says, with no map to ask about substitutions. This is the fallback for a
    // caller that has placements but no map folder: the restriction gate is unconditional and so still
    // correct, while a redirect without Config.json would be an assumption.
    public static HolidayPolicy FromClock() => new(HolidayUtil.ActiveHoliday, allowRedirects: false);

    // The full answer for a map: the clock, plus that map's Config.json.
    public static HolidayPolicy ForMap(string mapDirectory) =>
        ForMap(mapDirectory, HolidayUtil.ActiveHoliday);

    public static HolidayPolicy ForMap(string mapDirectory, ENPCHoliday active) =>
        new(active, UnturnedGodot.Data.MapCatalog.ReadAllowHolidayRedirects(mapDirectory));
}
