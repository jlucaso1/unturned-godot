using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

// The seam core/ reports through.
//
// It is a mutable static, which is the thing this repo otherwise avoids — so what these hold is the two
// properties that make it safe: it is silent until somebody installs a sink, and it can always be put
// back. A sink that could not be restored would leak one test's recorder into the next test's run.
[Collection(ProcessStateCollection.Name)]
public class HostLogTests
{
    [Fact]
    public void IsSilentUntilASinkIsInstalled()
    {
        IHostLog previous = HostLog.Sink;
        HostLog.Sink = null!;
        try
        {
            // Nothing to assert but the absence of a throw: an unset sink must be a no-op, because a
            // load-time job must not fail on the way it reports.
            HostLog.Print("into the void");
            HostLog.Warn("also the void");
            HostLog.Error("still the void");
        }
        finally
        {
            HostLog.Sink = previous;
        }
    }

    [Fact]
    public void AnInstalledSinkSeesEachChannelSeparately()
    {
        var log = new RecordingHostLog();
        IHostLog previous = HostLog.Sink;
        HostLog.Sink = log;
        try
        {
            HostLog.Print("progress");
            HostLog.Warn("skipped something");
            HostLog.Error("could not read it");
        }
        finally
        {
            HostLog.Sink = previous;
        }

        Assert.Equal(new[] { "progress" }, log.Prints);
        Assert.Equal(new[] { "skipped something" }, log.Warnings);
        Assert.Equal(new[] { "could not read it" }, log.Errors);
    }

    // Clearing the sink means "stop talking", not "crash on the next line".
    [Fact]
    public void ClearingTheSinkRestoresSilenceRatherThanThrowing()
    {
        var log = new RecordingHostLog();
        IHostLog previous = HostLog.Sink;
        try
        {
            HostLog.Sink = log;
            HostLog.Print("heard");
            HostLog.Sink = null!;
            HostLog.Print("not heard");
        }
        finally
        {
            HostLog.Sink = previous;
        }

        Assert.Equal(new[] { "heard" }, log.Prints);
    }
}
