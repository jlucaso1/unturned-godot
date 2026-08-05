using UnturnedGodot.DevConsole;
using Xunit;

namespace UnturnedGodot.Tests;

public class ConsoleHistoryTests
{
    [Fact]
    public void TheArrowsWalkBackwardsThenForwardsAgain()
    {
        var history = new ConsoleHistory();
        history.Add("foliage.enabled 0");
        history.Add("perf");

        Assert.Equal("perf", history.Previous());
        Assert.Equal("foliage.enabled 0", history.Previous());
        // Nothing further back: the caller keeps whatever is already in the box rather than having it
        // cleared by an arrow that had nowhere to go.
        Assert.Null(history.Previous());

        Assert.Equal("perf", history.Next());
        // Stepping past the newest line is the empty box the caller started from, which is a value —
        // and has to be told apart from "there is nothing there", which is null.
        Assert.Equal("", history.Next());
        Assert.Null(history.Next());
    }

    [Fact]
    public void ANewEntryPutsTheArrowsBackAtTheEnd()
    {
        var history = new ConsoleHistory();
        history.Add("one");
        history.Add("two");
        Assert.Equal("two", history.Previous());

        history.Add("three");

        Assert.Equal("three", history.Previous());
    }

    // Holding a toggle down — off, on, off, on — must not bury the line before it under copies.
    [Fact]
    public void ConsecutiveDuplicatesAreNotKept()
    {
        var history = new ConsoleHistory();
        history.Add("objects.trees.enabled 0");
        history.Add(" objects.trees.enabled 0 ");
        history.Add("objects.trees.enabled 1");
        history.Add("objects.trees.enabled 0");

        Assert.Equal(new[]
        {
            "objects.trees.enabled 0",
            "objects.trees.enabled 1",
            "objects.trees.enabled 0",
        }, history.Entries);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankLinesAreNotRemembered(string line)
    {
        var history = new ConsoleHistory();
        history.Add("perf");
        history.Add(line);

        Assert.Single(history.Entries);
        // ...but they still put the arrows back at the end, because pressing Enter on an empty box is
        // how somebody signals they are done scrolling through it.
        Assert.Equal("perf", history.Previous());
    }

    [Fact]
    public void ALongSessionCannotGrowItWithoutLimit()
    {
        var history = new ConsoleHistory();
        for (int i = 0; i < 300; i++)
            history.Add($"r.scale {i}");

        Assert.Equal(128, history.Entries.Count);
        Assert.Equal("r.scale 299", history.Entries[^1]);
        Assert.Equal("r.scale 172", history.Entries[0]);
    }
}
