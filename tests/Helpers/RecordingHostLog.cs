using System.Collections.Generic;

namespace UnturnedGodot.Tests.Helpers;

// Reads back what core/ reported through HostLog.
//
// Several of the one-time jobs answer "nothing" for two different reasons — the bundle had no such asset,
// or the bundle could not be read at all — and only the log tells them apart. A test that cannot see it
// can only assert the empty result, which passes just as well when the reason is wrong.
public sealed class RecordingHostLog : IHostLog
{
    public List<string> Prints { get; } = new();

    public List<string> Warnings { get; } = new();

    public List<string> Errors { get; } = new();

    public void Print(string message) => Prints.Add(message);

    public void Warn(string message) => Warnings.Add(message);

    public void Error(string message) => Errors.Add(message);
}
