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

    // The shape a measurement recipe is actually written in — one command per line, in a note or in
    // this repo's own docs — is the shape it gets copied in.
    [Fact]
    public void NewlinesSeparateStatements() =>
        Assert.Equal(new[] { "terrain.enabled 0", "perf", "terrain.enabled 1", "perf" },
            ConsoleCommandLine.Split("terrain.enabled 0\nperf\r\nterrain.enabled 1\rperf"));

    // Each line's note goes with the line it annotated. Ending the whole paste at the first `//` would
    // silently drop every step under an annotated one.
    [Fact]
    public void ACommentEndsItsOwnLineAndNotThePaste() =>
        Assert.Equal(new[] { "terrain.enabled 0", "perf" },
            ConsoleCommandLine.Split("terrain.enabled 0 // the A side\n// now read it\nperf"));

    // An unterminated quote is read the obvious way rather than refused (see below) — but only as far as
    // its own line, or one stray quote in a pasted block eats every command after it.
    [Fact]
    public void AnUnterminatedQuoteStopsAtItsLine() =>
        Assert.Equal(new[] { "echo \"open", "perf" }, ConsoleCommandLine.Split("echo \"open\nperf"));

    // The prompt is a LineEdit and cannot hold a newline, so a pasted block has to become one line. It
    // becomes the one the console already runs: the same statements, `;` separated.
    [Fact]
    public void APastedBlockFlattensOntoTheSemicolonGrammar()
    {
        Assert.Equal("terrain.enabled 0; perf; terrain.enabled 1; perf",
            ConsoleCommandLine.Flatten("terrain.enabled 0\nperf\nterrain.enabled 1\nperf\n"));
        // And what comes back out of it is what went in.
        Assert.Equal(new[] { "terrain.enabled 0", "perf", "terrain.enabled 1", "perf" },
            ConsoleCommandLine.Split(ConsoleCommandLine.Flatten(
                "terrain.enabled 0 // A\nperf\n\nterrain.enabled 1 // B\nperf")));
    }

    // A quoted `;` survives the round trip, so a value carrying one is not cut in half by having been
    // pasted rather than typed.
    [Fact]
    public void FlatteningKeepsAQuotedSeparatorWhole() =>
        Assert.Equal(new[] { "echo \"a; b\"", "perf" },
            ConsoleCommandLine.Split(ConsoleCommandLine.Flatten("echo \"a; b\"\nperf")));

    // The two separators disagree about quotes — a line break ends one, a `;` does not — so flattening
    // has to close what the break closed. Joining naively gives `echo "open; perf`, where the separator
    // is inside the quote and the second command has vanished: the newline rule undone by the step that
    // exists to preserve it.
    [Fact]
    public void FlatteningClosesAQuoteTheLineBreakHadClosed()
    {
        Assert.Equal("echo \"open\"; perf", ConsoleCommandLine.Flatten("echo \"open\nperf"));
        Assert.Equal(new[] { "echo \"open\"", "perf" },
            ConsoleCommandLine.Split(ConsoleCommandLine.Flatten("echo \"open\nperf")));
        // And closing it is free: an unterminated quote already ran to the end of its statement.
        Assert.Equal(ConsoleCommandLine.Words("echo \"open"), ConsoleCommandLine.Words("echo \"open\""));
    }

    [Fact]
    public void FlatteningNothingIsNothing() =>
        Assert.Equal("", ConsoleCommandLine.Flatten("\n  \n// only a note\n"));

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
