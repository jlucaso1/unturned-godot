using Xunit;

namespace UnturnedGodot.Tests;

// The numeric half of the environment reader.
//
// There were four private copies of "parse it, clamp it" — the foliage builder, the foliage streamer, the
// object builder and the GPU benchmark — and they did not agree. The benchmark's had no clamp at all, so
// a mistyped UG_SHADOW_DIST was accepted as a setting and the run reported a frame time for something
// nobody asked for. That is the failure mode these pin: a tuning value is read by a measurement run, and
// a value read wrongly invalidates the run rather than failing it.
public class EnvOptionTests
{
    [Theory]
    [InlineData("7", 7)]
    [InlineData("  7  ", 7)]
    [InlineData("+7", 7)]
    [InlineData("0", 0)]
    public void AWholeNumberInRangeIsTakenAsWritten(string value, int expected)
    {
        Assert.Equal(expected, EnvOption.Whole(value, whenUnset: 3, min: 0, max: 10));
    }

    // Clamped rather than rejected: every one of these bounds a resource, and the useful answer to
    // "1000000 decode workers" is the most the caller supports.
    [Theory]
    [InlineData("1000000", 10)]
    [InlineData("-50", 0)]
    public void AWholeNumberOutsideTheRangeIsClampedToIt(string value, int expected)
    {
        Assert.Equal(expected, EnvOption.Whole(value, whenUnset: 3, min: 0, max: 10));
    }

    // An unreadable value is the DEFAULT, never an end of the range. A typo must not silently mean
    // "as much as possible" — that is the difference between a run that is wrong and one that is normal.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("lots")]
    [InlineData("7.5")]
    [InlineData("0x10")]
    public void AnUnreadableWholeNumberFallsBackToTheDefault(string? value)
    {
        Assert.Equal(3, EnvOption.Whole(value, whenUnset: 3, min: 0, max: 10));
    }

    [Fact]
    public void TheWideRangeIsAvailableForCountsMeasuredInTriangles()
    {
        Assert.Equal(5_000_000_000L,
            EnvOption.Whole64("5000000000", whenUnset: 0, min: 0, max: long.MaxValue));
        Assert.Equal(0L, EnvOption.Whole64("nonsense", whenUnset: 0, min: 0, max: long.MaxValue));
    }

    [Theory]
    [InlineData("1.5", 1.5f)]
    [InlineData("-1", -1f)]
    [InlineData("160", 160f)]
    [InlineData("1e2", 100f)]
    public void AMeasurementInRangeIsTakenAsWritten(string value, float expected)
    {
        Assert.Equal(expected, EnvOption.Number(value, whenUnset: 0f, min: -10f, max: 1000f));
    }

    [Theory]
    [InlineData("99999", 1000f)]
    [InlineData("-99999", -10f)]
    public void AMeasurementOutsideTheRangeIsClampedToIt(string value, float expected)
    {
        Assert.Equal(expected, EnvOption.Number(value, whenUnset: 0f, min: -10f, max: 1000f));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("far")]
    public void AnUnreadableMeasurementFallsBackToTheDefault(string? value)
    {
        Assert.Equal(5f, EnvOption.Number(value, whenUnset: 5f, min: -10f, max: 1000f));
    }

    // NaN is treated as unreadable rather than clamped: Math.Clamp THROWS on it, and a caller in the
    // middle of a load has to get the default instead of an exception out of a tuning knob.
    [Theory]
    [InlineData("nan")]
    [InlineData("NaN")]
    public void NotANumberIsTheDefaultRatherThanAThrow(string value)
    {
        Assert.Equal(5f, EnvOption.Number(value, whenUnset: 5f, min: -10f, max: 1000f));
    }

    // Parsed invariantly, whatever the runtime's culture is. These are values typed into a shell or a CI
    // file, and a runtime that read "1.5" as fifteen would silently change what a run measured.
    [Fact]
    public void AMeasurementIsReadTheSameWayOnEveryMachine()
    {
        Assert.Equal(1.5f, EnvOption.Number("1.5", whenUnset: 0f, min: 0f, max: 10f));
        // A comma is not a decimal point here; it is unreadable, so the default stands.
        Assert.Equal(0f, EnvOption.Number("1,5", whenUnset: 0f, min: 0f, max: 10f));
    }

    // The flag half is EnvFlag's, and this is the one entry point new code reaches for. What matters is
    // that it is the SAME function: the closed set, case-insensitively, with on/off among the spellings.
    // ObjectsBuilder used to keep its own copy that accepted neither `on` nor `On`, so UG_OBJECT_LOD=On
    // silently took the default — the exact bug EnvFlag's header says it was written to end.
    [Theory]
    [InlineData("on", true)]
    [InlineData("On", true)]
    [InlineData("OFF", false)]
    [InlineData("True", true)]
    [InlineData("no", false)]
    public void TheFlagReaderIsTheSameOneTheRestOfTheProjectUses(string value, bool expected)
    {
        Assert.Equal(expected, EnvOption.IsOn(value, whenUnset: !expected));
        Assert.Equal(EnvFlag.IsOn(value, whenUnset: !expected), EnvOption.IsOn(value, whenUnset: !expected));
    }

    [Fact]
    public void AnUnsetFlagIsItsOwnDefault()
    {
        Assert.True(EnvOption.IsOn(null, whenUnset: true));
        Assert.False(EnvOption.IsOn(null, whenUnset: false));
        Assert.True(EnvOption.IsOn("perhaps", whenUnset: true));
    }
}
