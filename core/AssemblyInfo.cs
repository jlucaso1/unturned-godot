using System.Runtime.CompilerServices;

// A few defensive helpers are unreachable through the public API; tests exercise them directly.
[assembly: InternalsVisibleTo("UnturnedGodot.Tests")]
