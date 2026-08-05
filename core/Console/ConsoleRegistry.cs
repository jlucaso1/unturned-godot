using System;
using System.Collections.Generic;

namespace UnturnedGodot.DevConsole;

// A command's implementation: the words after its name, and whatever it wants to print.
public delegate IEnumerable<ConsoleLine> ConsoleCommand(IReadOnlyList<string> arguments);

// Everything the console can be asked to do, and the only thing that decides what a typed line means.
//
// It is engine-free on purpose. Names, values, ranges, parsing, the built-in commands and the wording of
// every refusal are decided here, where a test can drive them without a window, a renderer or a map; the
// game half (src/Console) supplies delegates that move engine state and nothing else. That split is what
// makes "does `objects.trees.enabled 2` say something useful" a unit test rather than something somebody
// has to remember to try by hand.
//
// Lookup is case-insensitive because nobody wants to hold shift to switch off a tree, and the names are
// dotted rather than nested because a flat namespace with a good `find` beats a hierarchy you have to
// navigate: `find shadow` answers "what can I turn off about shadows" in one keystroke.
public sealed class ConsoleRegistry
{
    private readonly record struct Entry(string Name, string Summary, string Usage, ConsoleCommand Run);

    private readonly Dictionary<string, ConsoleVariable> _variables =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> _commands = new(StringComparer.OrdinalIgnoreCase);

    // How many near-misses an unknown name is answered with. Enough to cover a half-remembered name,
    // few enough that the answer is still readable at a glance.
    private const int MaximumSuggestions = 6;

    public ConsoleRegistry()
    {
        Add("help", "What a name means: `help`, or `help <name>` for one variable or command.",
            "help [name]", ShowHelp);
        Add("list", "Every variable and its current value; `list <prefix>` narrows it.", "list [prefix]",
            ListVariables);
        Add("find", "Every variable or command whose name or description mentions the text.",
            "find <text>", FindMentions);
        Add("reset", "Put a variable back to its default value; `reset all` for every one of them.",
            "reset <name|all>", ResetVariable);
    }

    public IEnumerable<ConsoleVariable> Variables => _variables.Values;

    public ConsoleVariable Add(ConsoleVariable variable)
    {
        if (_variables.ContainsKey(variable.Name) || _commands.ContainsKey(variable.Name))
            throw new ArgumentException($"'{variable.Name}' is already registered.", nameof(variable));
        _variables.Add(variable.Name, variable);
        return variable;
    }

    public void Add(string name, string summary, string usage, ConsoleCommand run)
    {
        if (_variables.ContainsKey(name) || _commands.ContainsKey(name))
            throw new ArgumentException($"'{name}' is already registered.", nameof(name));
        _commands.Add(name, new Entry(name, summary, usage, run));
    }

    public ConsoleVariable? Variable(string name) =>
        _variables.TryGetValue(name, out ConsoleVariable? variable) ? variable : null;

    // Runs a whole line: every `;`-separated statement on it, in order, each one's output following the
    // last. A statement that fails does not stop the ones after it — half of a configuration applied and
    // the reason the other half was not is more useful than an all-or-nothing refusal.
    public IReadOnlyList<ConsoleLine> Execute(string line)
    {
        var output = new List<ConsoleLine>();
        foreach (string statement in ConsoleCommandLine.Split(line))
            Run(ConsoleCommandLine.Words(statement), output);
        return output;
    }

    private void Run(IReadOnlyList<string> words, List<ConsoleLine> output)
    {
        if (words.Count == 0)
            return;

        string name = words[0];
        var arguments = new List<string>(words.Count - 1);
        for (int i = 1; i < words.Count; i++)
            arguments.Add(words[i]);

        if (_commands.TryGetValue(name, out Entry command))
        {
            output.AddRange(command.Run(arguments));
            return;
        }
        if (_variables.TryGetValue(name, out ConsoleVariable? variable))
        {
            Assign(variable, arguments, output);
            return;
        }

        output.Add(ConsoleLine.Failure($"'{name}' is not a variable or a command."));
        List<string> near = Suggest(name);
        if (near.Count > 0)
            output.Add(ConsoleLine.Reply("Did you mean: " + string.Join(", ", near)));
    }

    private static void Assign(ConsoleVariable variable, IReadOnlyList<string> arguments,
        List<ConsoleLine> output)
    {
        if (arguments.Count == 0)
        {
            // Naming a variable and nothing else asks what it is, which is how you check what a session
            // is actually running before you trust a number it produced.
            output.Add(ConsoleLine.Reply(variable.Describe()));
            output.Add(ConsoleLine.Reply("  " + variable.Summary));
            return;
        }
        if (arguments.Count > 1)
        {
            output.Add(ConsoleLine.Failure(
                $"{variable.Name} takes one value; got {arguments.Count}."));
            return;
        }
        if (!variable.TrySet(arguments[0], out string failure))
        {
            output.Add(ConsoleLine.Failure(failure));
            return;
        }
        output.Add(ConsoleLine.Reply($"{variable.Name} = {variable.Value}"));
        if (variable.Apply() is { Length: > 0 } notice)
            output.Add(ConsoleLine.Notice(notice));
    }

    // Pushes every non-default value at the world again. The world is rebuilt on every map load, so
    // without this a configuration set up on one map would quietly be gone on the next — and a frame
    // measured against it would be measuring the defaults while its scrollback still said otherwise.
    public IReadOnlyList<ConsoleLine> Reapply()
    {
        var output = new List<ConsoleLine>();
        foreach (ConsoleVariable variable in Sorted())
        {
            if (variable.IsDefault)
                continue;
            if (variable.Apply() is { Length: > 0 } notice)
                output.Add(ConsoleLine.Notice(notice));
        }
        return output;
    }

    // Every name that starts with `prefix`, variables and commands together, sorted — what tab
    // completion offers and what `list` walks.
    public IReadOnlyList<string> Names(string prefix)
    {
        var names = new List<string>();
        foreach (string name in _variables.Keys)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                names.Add(name);
        foreach (string name in _commands.Keys)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                names.Add(name);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    // The longest head every candidate shares, which is what a tab press fills in. Completing to that
    // and printing the candidates is the behaviour every shell has trained everyone to expect.
    public static string CommonPrefix(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
            return "";
        string head = names[0];
        for (int i = 1; i < names.Count; i++)
        {
            string other = names[i];
            int shared = 0;
            while (shared < head.Length && shared < other.Length
                && char.ToLowerInvariant(head[shared]) == char.ToLowerInvariant(other[shared]))
                shared++;
            head = head[..shared];
        }
        return head;
    }

    private List<ConsoleVariable> Sorted()
    {
        var sorted = new List<ConsoleVariable>(_variables.Values);
        sorted.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return sorted;
    }

    private List<string> Suggest(string name)
    {
        var near = new List<string>();
        foreach (string candidate in _variables.Keys)
            if (candidate.Contains(name, StringComparison.OrdinalIgnoreCase))
                near.Add(candidate);
        foreach (string candidate in _commands.Keys)
            if (candidate.Contains(name, StringComparison.OrdinalIgnoreCase))
                near.Add(candidate);
        near.Sort(StringComparer.OrdinalIgnoreCase);
        if (near.Count > MaximumSuggestions)
            near.RemoveRange(MaximumSuggestions, near.Count - MaximumSuggestions);
        return near;
    }

    private IEnumerable<ConsoleLine> ShowHelp(IReadOnlyList<string> arguments)
    {
        if (arguments.Count > 0)
        {
            string name = arguments[0];
            if (_variables.TryGetValue(name, out ConsoleVariable? variable))
            {
                yield return ConsoleLine.Reply(variable.Describe());
                yield return ConsoleLine.Reply("  " + variable.Summary);
                yield break;
            }
            if (_commands.TryGetValue(name, out Entry command))
            {
                yield return ConsoleLine.Reply(command.Usage);
                yield return ConsoleLine.Reply("  " + command.Summary);
                yield break;
            }
            yield return ConsoleLine.Failure($"'{name}' is not a variable or a command.");
            yield break;
        }

        yield return ConsoleLine.Reply("Commands:");
        var names = new List<string>(_commands.Keys);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
            yield return ConsoleLine.Reply($"  {_commands[name].Usage,-24} {_commands[name].Summary}");
        yield return ConsoleLine.Reply(
            $"{_variables.Count} variables — `list` shows them, `find <text>` searches them.");
    }

    private IEnumerable<ConsoleLine> ListVariables(IReadOnlyList<string> arguments)
    {
        string prefix = arguments.Count > 0 ? arguments[0] : "";
        int shown = 0;
        foreach (ConsoleVariable variable in Sorted())
        {
            if (!variable.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            shown++;
            // A changed value is worth spotting in a long list: it is the reason the frame in front of
            // you does not look like the one the defaults produce.
            yield return ConsoleLine.Reply(variable.IsDefault
                ? $"  {variable.Name} = {variable.Value}"
                : $"* {variable.Name} = {variable.Value}  (default {variable.DefaultText})");
        }
        yield return shown > 0
            ? ConsoleLine.Reply($"{shown} variables; * marks the ones that are not at their default.")
            : ConsoleLine.Notice($"No variable starts with '{prefix}'.");
    }

    private IEnumerable<ConsoleLine> FindMentions(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            yield return ConsoleLine.Failure("find <text>");
            yield break;
        }

        string text = arguments[0];
        int found = 0;
        foreach (ConsoleVariable variable in Sorted())
        {
            if (!Mentions(variable.Name, text) && !Mentions(variable.Summary, text))
                continue;
            found++;
            yield return ConsoleLine.Reply($"{variable.Name} = {variable.Value}");
            yield return ConsoleLine.Reply("  " + variable.Summary);
        }
        var names = new List<string>(_commands.Keys);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            Entry command = _commands[name];
            if (!Mentions(command.Name, text) && !Mentions(command.Summary, text))
                continue;
            found++;
            yield return ConsoleLine.Reply(command.Usage);
            yield return ConsoleLine.Reply("  " + command.Summary);
        }
        if (found == 0)
            yield return ConsoleLine.Notice($"Nothing mentions '{text}'.");
    }

    private static bool Mentions(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private IEnumerable<ConsoleLine> ResetVariable(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            yield return ConsoleLine.Failure("reset <name|all>");
            yield break;
        }

        if (string.Equals(arguments[0], "all", StringComparison.OrdinalIgnoreCase))
        {
            int restored = 0;
            foreach (ConsoleVariable variable in Sorted())
            {
                if (variable.IsDefault)
                    continue;
                restored++;
                variable.SetToDefault();
                if (variable.Apply() is { Length: > 0 } notice)
                    yield return ConsoleLine.Notice(notice);
            }
            yield return ConsoleLine.Reply(restored == 0
                ? "Every variable was already at its default."
                : $"{restored} variables restored to their defaults.");
            yield break;
        }

        if (!_variables.TryGetValue(arguments[0], out ConsoleVariable? target))
        {
            yield return ConsoleLine.Failure($"'{arguments[0]}' is not a variable.");
            yield break;
        }
        target.SetToDefault();
        yield return ConsoleLine.Reply($"{target.Name} = {target.Value}");
        if (target.Apply() is { Length: > 0 } applied)
            yield return ConsoleLine.Notice(applied);
    }
}
