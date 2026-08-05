using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Every console line the game prints, and the ring buffer behind them.
//
// The ring is not decoration: the bug-repro dump carries it, so whoever reads a dump gets the run-up to
// the failure rather than only the moment of it. That makes two properties load-bearing — the tail has to
// survive being written past (it is a fixed ring, and a session prints far more than it holds), and it
// has to stay ordered, because a scrambled run-up is worse than none.
//
// The stamp is the other half. Godot does not timestamp its prints, and almost everything worth reading
// in this log is about WHEN something happened relative to something else: how long the decode ran before
// the scene appeared, whether a worker was still writing when the game quit.
public class LogTests : TestClass
{
    public LogTests(Node testScene) : base(testScene) { }

    // Lines come back in the order they were printed, and carry their text.
    [Test]
    public void ThePrintedLinesAreRememberedInOrder()
    {
        string marker = "log-test-" + System.Guid.NewGuid().ToString("N");
        Log.Print(marker + "-first");
        Log.Print(marker + "-second");

        List<string> tail = Log.Tail();
        int first = tail.FindIndex(l => l.Contains(marker + "-first"));
        int second = tail.FindIndex(l => l.Contains(marker + "-second"));

        Assert.True(first >= 0, "the first line was not remembered");
        Assert.True(second > first, "the tail came back out of order");
    }

    // Every line is stamped with how long the process has been up. Wall-clock answers none of the
    // questions this log exists to answer; elapsed time answers all of them by subtraction.
    [Test]
    public void EveryLineCarriesItsElapsedTime()
    {
        string marker = "log-stamp-" + System.Guid.NewGuid().ToString("N");
        Log.Print(marker);

        string line = Log.Tail().FindLast(l => l.Contains(marker))!;

        Assert.StartsWith("[", line, System.StringComparison.Ordinal);
        int close = line.IndexOf(']');
        Assert.True(close > 1, $"no stamp on '{line}'");
        Assert.True(double.TryParse(line[1..close].Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double seconds),
            $"the stamp is not a number: '{line[1..close]}'");
        Assert.True(seconds >= 0);
    }

    // Errors and warnings are remembered too. A dump that carried only the ordinary prints would drop the
    // one line most worth reading.
    [Test]
    public void ErrorsAndWarningsAreRememberedAsWell()
    {
        string marker = "log-err-" + System.Guid.NewGuid().ToString("N");
        Log.PrintErr(marker + "-error");
        Log.PushWarning(marker + "-warning");

        List<string> tail = Log.Tail();
        Assert.Contains(tail, l => l.Contains(marker + "-error"));
        Assert.Contains(tail, l => l.Contains(marker + "-warning"));
    }

    // The ring is bounded and survives being written past — a session prints far more than it holds, and
    // a dump taken late must still carry the most recent lines rather than the oldest.
    [Test]
    public void TheRingIsBoundedAndKeepsTheMostRecentLines()
    {
        string marker = "log-ring-" + System.Guid.NewGuid().ToString("N");
        for (int i = 0; i < 400; i++) // comfortably past the 256-line ring
            Log.Print($"{marker}-{i}");

        List<string> tail = Log.Tail();

        Assert.True(tail.Count <= 256, $"the ring grew to {tail.Count} lines");
        Assert.Contains(tail, l => l.Contains($"{marker}-399"));   // the newest survived
        Assert.DoesNotContain(tail, l => l.Contains($"{marker}-0")); // the oldest was dropped
    }
}
