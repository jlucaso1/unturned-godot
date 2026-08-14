using Xunit;

namespace UnturnedGodot.Tests.Helpers;

// Serializes the test classes that touch process-global state.
//
// xUnit runs each test class in parallel with the others by default, which is right for a suite that is
// almost entirely pure functions over their arguments. Two things here are not: HostLog.Sink, which core/
// reports through, and the environment variables that configure a load. A class that swaps either one is
// changing what a class running beside it observes — a recorder installed by one test would collect
// another's lines, and the assertion that fails would be in whichever test lost the race.
//
// Naming the same collection puts them in one queue. It costs the parallelism of a handful of classes out
// of a hundred and change, which is not measurable in a suite that runs in seconds.
[CollectionDefinition(Name)]
public sealed class ProcessStateCollection
{
    public const string Name = "process-global state";
}
