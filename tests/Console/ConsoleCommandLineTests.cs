using System.Collections.Generic;
using UnturnedGodot.DevConsole;
using Xunit;

namespace UnturnedGodot.Tests;

// The grammar the console reads. Small on purpose, so what it does NOT do is as much of the contract as
// what it does: no substitution, no globbing, nothing that can turn a typed line into a different one.
public class ConsoleCommandLineTests
{
    [Fact]
    public void APlainLineIsOneStatement() =>
        Assert.Equal(new[] { "foliage.enabled 0" },
            ConsoleCommandLine.Split("foliage.enabled 0"));

    // The reason `;` exists: three toggles between two frames rather than three round trips through the
    // input box, which would be three different frames and so three different measurements.
    [Fact]
    public void SemicolonsSeparateStatements() =>
        Assert.Equal(new[] { "foliage.enabled 0", "objects.trees.enabled 0", "perf" },
            ConsoleCommandLine.Split("foliage.enabled 0; objects.trees.enabled 0 ;perf"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";;;")]
    [InlineData("  ;  ; ")]
    public void NothingTypedIsNoStatements(string line) =>
        Assert.Empty(ConsoleCommandLine.Split(line));

    // A configuration pasted out of a note keeps the note attached to it.
    [Fact]
    public void ACommentEndsTheLine() =>
        Assert.Equal(new[] { "foliage.enabled 0" },
            ConsoleCommandLine.Split("foliage.enabled 0 // costs 4 ms at the spawn"));

    [Fact]
    public void ACommentAloneLeavesNothingToRun() =>
        Assert.Empty(ConsoleCommandLine.Split("// measured on PEI, 1600x900"));

    // A quoted `;` is a character, not a separator, or a value could be cut in half by its own contents.
    [Fact]
    public void QuotesProtectSeparatorsAndComments()
    {
        Assert.Equal(new[] { "echo \"a; b\"" }, ConsoleCommandLine.Split("echo \"a; b\""));
        Assert.Equal(new[] { "echo \"a // b\"" }, ConsoleCommandLine.Split("echo \"a // b\""));
    }

    [Fact]
    public void WordsSplitOnAnyRunOfWhitespace() =>
        Assert.Equal(new[] { "sun.shadows.distance", "32" },
            ConsoleCommandLine.Words("  sun.shadows.distance \t 32  "));

    [Fact]
    public void QuotesGroupWordsAndDisappear() =>
        Assert.Equal(new[] { "find", "shadow pass" }, ConsoleCommandLine.Words("find \"shadow pass\""));

    [Fact]
    public void AnEscapedQuoteIsALiteralOne() =>
        Assert.Equal(new[] { "echo", "say \"hello\"" },
            ConsoleCommandLine.Words("echo \"say \\\"hello\\\"\""));

    // An empty pair of quotes IS a word — "the empty value" — and has to be told apart from the spaces
    // between words, which are not.
    [Fact]
    public void AnEmptyQuotedWordSurvives() =>
        Assert.Equal(new[] { "echo", "" }, ConsoleCommandLine.Words("echo \"\""));

    // Somebody is typing this live. Refusing the half-finished line they are still in the middle of is
    // worse than reading it the obvious way.
    [Fact]
    public void AnUnterminatedQuoteTakesTheRestOfTheStatement() =>
        Assert.Equal(new[] { "find", "shadow pass" }, ConsoleCommandLine.Words("find \"shadow pass"));

    [Fact]
    public void AnEmptyStatementHasNoWords() =>
        Assert.Empty(ConsoleCommandLine.Words("    "));

    // Quotes inside a word, rather than around it: `a"b"c` is one word, because splitting it would be
    // inventing a boundary nobody typed.
    [Fact]
    public void QuotesInsideAWordDoNotSplitIt()
    {
        List<string> words = ConsoleCommandLine.Words("a\"b c\"d");

        Assert.Equal(new[] { "ab cd" }, words);
    }
}
