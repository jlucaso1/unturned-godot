using Xunit;

namespace UnturnedGodot.Tests;

public class EnvFlagTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("  1  ")]
    public void RecognisesTheOnSpellings(string value)
    {
        Assert.True(EnvFlag.Parse(value));
        Assert.True(EnvFlag.IsOn(value, whenUnset: false));
        Assert.True(EnvFlag.IsOn(value, whenUnset: true));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("False")]
    [InlineData("no")]
    [InlineData("off")]
    [InlineData(" 0 ")]
    public void RecognisesTheOffSpellings(string value)
    {
        Assert.False(EnvFlag.Parse(value));
        Assert.False(EnvFlag.IsOn(value, whenUnset: false));
        Assert.False(EnvFlag.IsOn(value, whenUnset: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnsetFlagTakesItsDefault(string? value)
    {
        Assert.Null(EnvFlag.Parse(value));
        Assert.True(EnvFlag.IsOn(value, whenUnset: true));
        Assert.False(EnvFlag.IsOn(value, whenUnset: false));
    }

    // Guessing at a typo is how a flag ends up meaning its opposite, which is the whole failure this
    // replaces. An unrecognised value takes the documented default in both directions instead.
    [Theory]
    [InlineData("2")]
    [InlineData("ture")]
    [InlineData("enabled")]
    [InlineData("-1")]
    public void AnUnrecognisedValueTakesTheDefault_RatherThanBeingGuessedAt(string value)
    {
        Assert.Null(EnvFlag.Parse(value));
        Assert.True(EnvFlag.IsOn(value, whenUnset: true));
        Assert.False(EnvFlag.IsOn(value, whenUnset: false));
    }

    // The two bugs this exists to remove, stated as the behaviour that used to happen.
    [Fact]
    public void FalseNoLongerEnablesAnOptOutFlag()
    {
        // Was: `OS.GetEnvironment("UG_FOLIAGE_RESIDENCY") != "0"` — "false" is not "0", so residency ran.
        Assert.False(EnvFlag.IsOn("false", whenUnset: true));
    }

    [Fact]
    public void TrueNoLongerLeavesAnOptInFlagOff()
    {
        // Was: `OS.GetEnvironment("UG_NODE_MULTIMESH") == "1"` — "true" is not "1", so it stayed off.
        Assert.True(EnvFlag.IsOn("true", whenUnset: false));
    }
}
