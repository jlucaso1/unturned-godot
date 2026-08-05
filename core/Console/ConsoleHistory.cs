using System.Collections.Generic;

namespace UnturnedGodot.DevConsole;

// The lines already typed, and where the up/down arrows currently stand in them.
//
// This exists because the console's whole job is A/B: type a line, look at the frame time, put it back,
// look again. Doing that by retyping `objects.trees.enabled 1` every other minute is how a measurement
// session turns into a typing session, and how a typo silently measures something else.
//
// Consecutive duplicates are dropped so holding the same toggle down does not bury the line before it,
// and the ring is bounded so a long session cannot grow it without limit.
public sealed class ConsoleHistory
{
    private const int Capacity = 128;

    private readonly List<string> _entries = new();
    // Where the arrows are: an index into _entries, or Count when the caller is back at the line they
    // were typing rather than inside the history at all.
    private int _cursor;

    public IReadOnlyList<string> Entries => _entries;

    public void Add(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.Length > 0 && (_entries.Count == 0 || _entries[^1] != trimmed))
        {
            _entries.Add(trimmed);
            if (_entries.Count > Capacity)
                _entries.RemoveAt(0);
        }
        _cursor = _entries.Count;
    }

    // One step further back, or null at the oldest line — the caller keeps what is already in the box
    // rather than having it cleared by an arrow that had nowhere to go.
    public string? Previous()
    {
        if (_cursor == 0)
            return null;
        _cursor--;
        return _entries[_cursor];
    }

    // One step forward. Stepping past the newest line returns "" rather than null: that IS a value —
    // the empty box the caller started from — and it has to be told apart from "there is nothing there".
    public string? Next()
    {
        if (_cursor >= _entries.Count)
            return null;
        _cursor++;
        return _cursor == _entries.Count ? "" : _entries[_cursor];
    }
}
