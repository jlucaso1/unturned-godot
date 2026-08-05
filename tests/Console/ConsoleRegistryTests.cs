using System;
using System.Collections.Generic;
using System.Linq;
using UnturnedGodot.DevConsole;
using Xunit;

namespace UnturnedGodot.Tests;

// What a typed line means. This is the whole console apart from the engine bindings, so it is where the
// behaviour a person actually relies on lives: that a value took, that a refusal says why, and that a
// half-remembered name is answered with the real one instead of silence.
public class ConsoleRegistryTests
{
    private sealed class Recorder
    {
        public int Applications;
        public string? Notice;

        public string? Apply(ConsoleVariable variable)
        {
            Applications++;
            Values.Add(variable.Value);
            return Notice;
        }

        public List<string> Values { get; } = new();
    }

    private static string Text(IReadOnlyList<ConsoleLine> lines) =>
        string.Join("\n", lines.Select(line => line.Text));

    private static ConsoleRegistry WithFoliage(out Recorder recorder)
    {
        var console = new ConsoleRegistry();
        Recorder record = new();
        recorder = record;
        console.Add(ConsoleVariable.Switch("foliage.enabled", "Draws the grass and the pebbles.", true,
            record.Apply));
        return console;
    }

    [Fact]
    public void SettingAValueAppliesItAndSaysSo()
    {
        ConsoleRegistry console = WithFoliage(out Recorder recorder);

        IReadOnlyList<ConsoleLine> output = console.Execute("foliage.enabled 0");

        ConsoleLine only = Assert.Single(output);
        Assert.Equal(ConsoleSeverity.Reply, only.Severity);
        Assert.Equal("foliage.enabled = 0", only.Text);
        Assert.Equal(1, recorder.Applications);
        Assert.Equal(new[] { "0" }, recorder.Values);
    }

    // A binding that cannot reach its target says so, and the value is still kept: the world it names is
    // rebuilt on every map load, and Reapply is what hands the value to the next one.
    [Fact]
    public void ABindingsNoticeIsReportedWithoutLosingTheValue()
    {
        ConsoleRegistry console = WithFoliage(out Recorder recorder);
        recorder.Notice = "No foliage is loaded right now.";

        IReadOnlyList<ConsoleLine> output = console.Execute("foliage.enabled 0");

        Assert.Equal(2, output.Count);
        Assert.Equal(ConsoleSeverity.Notice, output[1].Severity);
        Assert.Equal("No foliage is loaded right now.", output[1].Text);
        Assert.False(console.Variable("foliage.enabled")!.AsBool);
    }

    [Fact]
    public void NamingAVariableAloneAsksWhatItIs()
    {
        ConsoleRegistry console = WithFoliage(out Recorder recorder);

        IReadOnlyList<ConsoleLine> output = console.Execute("foliage.enabled");

        Assert.Equal("foliage.enabled = 1  (default 1)", output[0].Text);
        Assert.Equal("  Draws the grass and the pebbles.", output[1].Text);
        Assert.Equal(0, recorder.Applications); // asking is not setting
    }

    [Fact]
    public void ARefusedValueIsReportedAndNothingIsApplied()
    {
        ConsoleRegistry console = WithFoliage(out Recorder recorder);

        ConsoleLine only = Assert.Single(console.Execute("foliage.enabled maybe"));

        Assert.Equal(ConsoleSeverity.Failure, only.Severity);
        Assert.Contains("not a switch", only.Text);
        Assert.Equal(0, recorder.Applications);
        Assert.True(console.Variable("foliage.enabled")!.AsBool);
    }

    [Fact]
    public void TwoValuesForOneVariableIsARefusalRatherThanAGuess()
    {
        ConsoleRegistry console = WithFoliage(out Recorder recorder);

        ConsoleLine only = Assert.Single(console.Execute("foliage.enabled 0 1"));

        Assert.Equal(ConsoleSeverity.Failure, only.Severity);
        Assert.Contains("takes one value", only.Text);
        Assert.Equal(0, recorder.Applications);
    }

    [Fact]
    public void AnUnknownNameIsAnsweredWithTheOnesItCouldHaveBeen()
    {
        ConsoleRegistry console = WithFoliage(out _);

        IReadOnlyList<ConsoleLine> output = console.Execute("foliage");

        Assert.Equal(ConsoleSeverity.Failure, output[0].Severity);
        Assert.Contains("'foliage' is not a variable or a command", output[0].Text);
        Assert.Contains("foliage.enabled", output[1].Text);
    }

    [Fact]
    public void AnUnknownNameWithNoNearMissesJustSaysSo()
    {
        ConsoleRegistry console = WithFoliage(out _);

        ConsoleLine only = Assert.Single(console.Execute("zzz"));

        Assert.Equal(ConsoleSeverity.Failure, only.Severity);
    }

    // The reason `;` exists: one keypress, one frame, several changes.
    [Fact]
    public void EveryStatementOnALineRuns()
    {
        ConsoleRegistry console = WithFoliage(out Recorder recorder);

        IReadOnlyList<ConsoleLine> output = console.Execute("foliage.enabled 0; foliage.enabled 1");

        Assert.Equal(new[] { "0", "1" }, recorder.Values);
        Assert.Equal(2, output.Count);
    }

    // A bad statement does not take the good ones with it: half a configuration applied, and the reason
    // the other half was not, beats an all-or-nothing refusal you then have to bisect by hand.
    [Fact]
    public void AFailedStatementDoesNotStopTheOnesAfterIt()
    {
        ConsoleRegistry console = WithFoliage(out Recorder recorder);

        console.Execute("foliage.enabled nope; foliage.enabled 0");

        Assert.Equal(new[] { "0" }, recorder.Values);
    }

    [Fact]
    public void NamesAreMatchedWithoutRegardToCase()
    {
        ConsoleRegistry console = WithFoliage(out Recorder recorder);

        console.Execute("FOLIAGE.Enabled 0");

        Assert.Equal(1, recorder.Applications);
    }

    [Fact]
    public void ANameCanOnlyBeRegisteredOnce()
    {
        ConsoleRegistry console = WithFoliage(out _);

        Assert.Throws<ArgumentException>(() =>
            console.Add(ConsoleVariable.Switch("foliage.enabled", "Again.", true, _ => null)));
        Assert.Throws<ArgumentException>(() =>
            console.Add("foliage.enabled", "Again.", "usage", _ => Array.Empty<ConsoleLine>()));
        // ...including against the built-in commands, in both directions.
        Assert.Throws<ArgumentException>(() =>
            console.Add(ConsoleVariable.Switch("help", "Not this.", true, _ => null)));
        Assert.Throws<ArgumentException>(() =>
            console.Add("help", "Not this either.", "help", _ => Array.Empty<ConsoleLine>()));
    }

    [Fact]
    public void AnUnregisteredNameResolvesToNothing() =>
        Assert.Null(new ConsoleRegistry().Variable("nothing.enabled"));

    [Fact]
    public void ACommandRunsWithTheWordsAfterItsName()
    {
        var console = new ConsoleRegistry();
        List<string>? seen = null;
        console.Add("echo", "Says it back.", "echo <text>", arguments =>
        {
            seen = new List<string>(arguments);
            return new[] { ConsoleLine.Reply(string.Join(" ", arguments)) };
        });

        ConsoleLine only = Assert.Single(console.Execute("echo one \"two three\""));

        Assert.Equal(new[] { "one", "two three" }, seen);
        Assert.Equal("one two three", only.Text);
    }

    [Fact]
    public void HelpWithoutAnArgumentListsTheCommandsAndCountsTheVariables()
    {
        ConsoleRegistry console = WithFoliage(out _);

        string output = Text(console.Execute("help"));

        Assert.Contains("help [name]", output);
        Assert.Contains("reset <name|all>", output);
        Assert.Contains("1 variables", output);
    }

    [Fact]
    public void HelpNamesAVariableACommandOrNeither()
    {
        ConsoleRegistry console = WithFoliage(out _);

        Assert.Contains("Draws the grass", Text(console.Execute("help foliage.enabled")));
        Assert.Contains("Put a variable back", Text(console.Execute("help reset")));

        ConsoleLine only = Assert.Single(console.Execute("help nonsense"));
        Assert.Equal(ConsoleSeverity.Failure, only.Severity);
    }

    [Fact]
    public void ListMarksWhatIsNoLongerAtItsDefault()
    {
        ConsoleRegistry console = WithFoliage(out _);
        console.Add(ConsoleVariable.Switch("water.enabled", "Sea plane.", true, _ => null));

        Assert.DoesNotContain(console.Execute("list"), line => line.Text.StartsWith("* ", StringComparison.Ordinal));

        console.Execute("foliage.enabled 0");
        string output = Text(console.Execute("list"));

        Assert.Contains("* foliage.enabled = 0  (default 1)", output);
        Assert.Contains("  water.enabled = 1", output);
        Assert.Contains("2 variables", output);
    }

    [Fact]
    public void ListNarrowsToAPrefixAndSaysWhenNothingMatches()
    {
        ConsoleRegistry console = WithFoliage(out _);
        console.Add(ConsoleVariable.Switch("water.enabled", "Sea plane.", true, _ => null));

        string narrowed = Text(console.Execute("list foliage"));
        Assert.Contains("foliage.enabled", narrowed);
        Assert.DoesNotContain("water.enabled", narrowed);

        ConsoleLine only = Assert.Single(console.Execute("list zz"));
        Assert.Equal(ConsoleSeverity.Notice, only.Severity);
    }

    [Fact]
    public void FindSearchesDescriptionsAsWellAsNames()
    {
        ConsoleRegistry console = WithFoliage(out _);

        // "pebbles" appears only in the description, which is the point: the name of the thing you want
        // is exactly what you do not know yet.
        Assert.Contains("foliage.enabled", Text(console.Execute("find pebbles")));
        // Commands are searched too.
        Assert.Contains("reset <name|all>", Text(console.Execute("find default")));

        ConsoleLine missing = Assert.Single(console.Execute("find zzz"));
        Assert.Equal(ConsoleSeverity.Notice, missing.Severity);

        ConsoleLine noArguments = Assert.Single(console.Execute("find"));
        Assert.Equal(ConsoleSeverity.Failure, noArguments.Severity);
    }

    [Fact]
    public void ResetRestoresOneVariableAndAppliesIt()
    {
        ConsoleRegistry console = WithFoliage(out Recorder recorder);
        console.Execute("foliage.enabled 0");

        IReadOnlyList<ConsoleLine> output = console.Execute("reset foliage.enabled");

        Assert.Equal("foliage.enabled = 1", output[0].Text);
        Assert.Equal(new[] { "0", "1" }, recorder.Values);
    }

    [Fact]
    public void ResetAllTouchesOnlyWhatWasChanged()
    {
        ConsoleRegistry console = WithFoliage(out Recorder recorder);
        console.Add(ConsoleVariable.Switch("water.enabled", "Sea plane.", true, _ => null));
        console.Execute("foliage.enabled 0");
        recorder.Values.Clear();

        string output = Text(console.Execute("reset all"));

        Assert.Equal(new[] { "1" }, recorder.Values);
        Assert.Contains("1 variables restored", output);

        // And says so plainly when there was nothing to restore, rather than reporting a change.
        Assert.Contains("already at its default", Text(console.Execute("reset all")));
    }

    [Fact]
    public void ResetReportsANoticeFromTheBindingAndRefusesWhatItCannotReset()
    {
        ConsoleRegistry console = WithFoliage(out Recorder recorder);
        console.Execute("foliage.enabled 0");
        recorder.Notice = "No foliage is loaded right now.";

        Assert.Contains("No foliage is loaded", Text(console.Execute("reset foliage.enabled")));
        console.Execute("foliage.enabled 0");
        Assert.Contains("No foliage is loaded", Text(console.Execute("reset all")));

        ConsoleLine unknown = Assert.Single(console.Execute("reset nonsense"));
        Assert.Equal(ConsoleSeverity.Failure, unknown.Severity);

        ConsoleLine noArguments = Assert.Single(console.Execute("reset"));
        Assert.Equal(ConsoleSeverity.Failure, noArguments.Severity);
    }

    // The map-load contract: a value set against one world is pushed at the next one, and a value that
    // was never changed is not — pushing defaults at a fresh world would be work for no difference.
    [Fact]
    public void ReapplyPushesOnlyWhatWasChanged()
    {
        ConsoleRegistry console = WithFoliage(out Recorder recorder);
        console.Add(ConsoleVariable.Switch("water.enabled", "Sea plane.", true, _ => null));

        Assert.Empty(console.Reapply());
        Assert.Equal(0, recorder.Applications);

        console.Execute("foliage.enabled 0");
        recorder.Values.Clear();
        recorder.Notice = "No foliage is loaded right now.";

        IReadOnlyList<ConsoleLine> output = console.Reapply();

        Assert.Equal(new[] { "0" }, recorder.Values);
        Assert.Equal(ConsoleSeverity.Notice, Assert.Single(output).Severity);
    }

    [Fact]
    public void NamesOffersVariablesAndCommandsTogether()
    {
        ConsoleRegistry console = WithFoliage(out _);

        Assert.Equal(new[] { "foliage.enabled" }, console.Names("foliage"));
        Assert.Contains("help", console.Names(""));
        Assert.Contains("foliage.enabled", console.Names(""));
        Assert.Empty(console.Names("zzz"));
    }

    [Fact]
    public void TheCommonPrefixIsWhatATabPressFillsIn()
    {
        Assert.Equal("", ConsoleRegistry.CommonPrefix(Array.Empty<string>()));
        Assert.Equal("objects.", ConsoleRegistry.CommonPrefix(
            new[] { "objects.enabled", "objects.small.enabled", "objects.trees.enabled" }));
        Assert.Equal("one", ConsoleRegistry.CommonPrefix(new[] { "one" }));
        Assert.Equal("", ConsoleRegistry.CommonPrefix(new[] { "one", "two" }));
        // Case-insensitively, like the lookup itself.
        Assert.Equal("Fol", ConsoleRegistry.CommonPrefix(new[] { "Foliage", "folly" }));
    }

    [Fact]
    public void EveryRegisteredVariableIsEnumerable()
    {
        ConsoleRegistry console = WithFoliage(out _);

        Assert.Equal("foliage.enabled", Assert.Single(console.Variables).Name);
    }
}
