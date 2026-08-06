using System;
using UnturnedGodot.DevConsole;
using Xunit;

namespace UnturnedGodot.Tests;

// One tunable: what it accepts, what it refuses, and what it says when it refuses.
//
// The refusals are the part worth testing. A console whose job is measurement has exactly one failure
// mode that matters — a value that did not take, on a frame you then wrote a number down about — so
// every path that does not change the value has to say so out loud.
public class ConsoleVariableTests
{
    private static ConsoleVariable Switch(bool value = true) =>
        ConsoleVariable.Switch("thing.enabled", "Draws the thing.", value, _ => null);

    [Theory]
    [InlineData("1")]
    [InlineData("on")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData(" on ")]
    public void ASwitchTakesEveryOnSpelling(string text)
    {
        ConsoleVariable variable = Switch(value: false);

        Assert.True(variable.TrySet(text, out string failure));
        Assert.Empty(failure);
        Assert.True(variable.AsBool);
        // Whatever spelling set it, it reads back as 0/1 — so `list` output can be pasted straight back.
        Assert.Equal("1", variable.Value);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("off")]
    [InlineData("false")]
    [InlineData("No")]
    public void ASwitchTakesEveryOffSpelling(string text)
    {
        ConsoleVariable variable = Switch();

        Assert.True(variable.TrySet(text, out _));
        Assert.False(variable.AsBool);
        Assert.Equal("0", variable.Value);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("maybe")]
    [InlineData("")]
    public void ASwitchRefusesAnythingElseAndKeepsItsValue(string text)
    {
        ConsoleVariable variable = Switch();

        Assert.False(variable.TrySet(text, out string failure));
        Assert.Contains("0/1", failure);
        Assert.True(variable.AsBool);
    }

    [Fact]
    public void AWholeNumberTakesItsRangeAndRefusesOutsideIt()
    {
        ConsoleVariable msaa = ConsoleVariable.Whole("r.msaa", "Antialiasing.", 0, 0, 3, _ => null);

        Assert.True(msaa.TrySet("3", out _));
        Assert.Equal(3, msaa.AsInt);

        // Refused rather than clamped. A clamp answers an out-of-range value with a frame that looks
        // exactly like the one before it, which is the one thing a measurement cannot survive.
        Assert.False(msaa.TrySet("4", out string failure));
        Assert.Contains("0..3", failure);
        Assert.Equal(3, msaa.AsInt);

        Assert.False(msaa.TrySet("-1", out _));
        Assert.False(msaa.TrySet("2.5", out string notWhole));
        Assert.Contains("whole number", notWhole);
        Assert.Equal(3, msaa.AsInt);
    }

    [Fact]
    public void ANumberParsesInvariantlyAndRefusesTheNonFinite()
    {
        ConsoleVariable scale = ConsoleVariable.Number("r.scale", "Resolution.", 1f, 0.25f, 2f, _ => null);

        Assert.True(scale.TrySet("0.5", out _));
        Assert.Equal(0.5f, scale.AsFloat);
        Assert.Equal("0.5", scale.Value);

        // TryParse accepts both of these happily, and either one poisons the viewport property it would
        // be written into for the rest of the session.
        Assert.False(scale.TrySet("NaN", out _));
        Assert.False(scale.TrySet("Infinity", out _));
        Assert.False(scale.TrySet("half", out string failure));
        Assert.Contains("not a number", failure);
        Assert.Equal(0.5f, scale.AsFloat);
    }

    [Fact]
    public void ADescriptionCarriesTheValueTheDefaultAndTheRange()
    {
        ConsoleVariable distance =
            ConsoleVariable.Number("sun.shadows.distance", "Cascade reach.", 64f, 1f, 1024f, _ => null);
        Assert.Equal("sun.shadows.distance = 64  (default 64, 1..1024)", distance.Describe());

        Assert.True(distance.TrySet("32", out _));
        Assert.Equal("sun.shadows.distance = 32  (default 64, 1..1024)", distance.Describe());

        // A switch's range is 0..1 and saying so tells nobody anything.
        Assert.Equal("thing.enabled = 1  (default 1)", Switch().Describe());
    }

    // The gap inside a choice's span is the whole reason it is not a range. Shadow cascades are 1, 2 or 4
    // and the engine has no three-cascade mode, so accepting 3 would leave the console remembering — and
    // `list` reporting, and a transcript recording — a number the renderer never ran.
    [Fact]
    public void AChoiceRefusesTheGapsInsideItsRange()
    {
        int applied = 0;
        ConsoleVariable cascades = ConsoleVariable.Choice("sun.shadows.cascades", "Splits.", 2,
            new[] { 1, 2, 4 }, v => { applied = v.AsInt; return null; });

        Assert.True(cascades.TrySet("4", out _));
        Assert.Equal(4, cascades.AsInt);

        Assert.False(cascades.TrySet("3", out string failure));
        Assert.Equal("3 is not one of sun.shadows.cascades's values (1/2/4).", failure);
        Assert.Equal(4, cascades.AsInt); // refused, so the last accepted value stands

        // Outside the span is still the range's refusal, which names the span rather than the values.
        Assert.False(cascades.TrySet("8", out string outside));
        Assert.Contains("outside", outside);

        Assert.Equal(0, applied); // TrySet stores; the registry is what applies
    }

    // The registration mistake the refusal above cannot catch: a default nothing can ever set it back to.
    [Fact]
    public void AChoiceRefusesADefaultOutsideItsOwnValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ConsoleVariable.Choice("sun.shadows.cascades", "Splits.", 3, new[] { 1, 2, 4 }, _ => null));
        Assert.Throws<ArgumentException>(() =>
            ConsoleVariable.Choice("sun.shadows.cascades", "Splits.", 2, Array.Empty<int>(), _ => null));
    }

    [Fact]
    public void AChoiceDescribesItsValuesRatherThanItsSpan()
    {
        ConsoleVariable cascades = ConsoleVariable.Choice("sun.shadows.cascades", "Splits.", 2,
            new[] { 1, 2, 4 }, _ => null);

        Assert.Equal("sun.shadows.cascades = 2  (default 2, one of 1/2/4)", cascades.Describe());
    }

    [Fact]
    public void ADefaultIsKnownAndRestorable()
    {
        ConsoleVariable variable = Switch();
        Assert.True(variable.IsDefault);

        Assert.True(variable.TrySet("0", out _));
        Assert.False(variable.IsDefault);

        variable.SetToDefault();
        Assert.True(variable.IsDefault);
        Assert.True(variable.AsBool);
        Assert.Equal("1", variable.DefaultText);
    }

    // Applying hands the variable itself to the binding, so one delegate can serve several variables and
    // read which one it is being asked about.
    [Fact]
    public void ApplyingHandsTheVariableToItsBindingAndReturnsWhateverItSaid()
    {
        ConsoleVariable? seen = null;
        ConsoleVariable variable = ConsoleVariable.Switch("water.enabled", "Sea plane.", true, asked =>
        {
            seen = asked;
            return asked.AsBool ? null : "nothing is loaded";
        });

        Assert.Null(variable.Apply());
        Assert.Same(variable, seen);

        Assert.True(variable.TrySet("0", out _));
        Assert.Equal("nothing is loaded", variable.Apply());
    }

    [Fact]
    public void TheKindIsWhatItWasBuiltAs()
    {
        Assert.Equal(ConsoleValueKind.Switch, Switch().Kind);
        Assert.Equal(ConsoleValueKind.Whole,
            ConsoleVariable.Whole("a", "b", 1, 0, 2, _ => null).Kind);
        Assert.Equal(ConsoleValueKind.Number,
            ConsoleVariable.Number("a", "b", 1f, 0f, 2f, _ => null).Kind);
    }
}
