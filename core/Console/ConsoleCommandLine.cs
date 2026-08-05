using System.Collections.Generic;
using System.Text;

namespace UnturnedGodot.DevConsole;

// Turns what somebody typed into statements and words.
//
// The grammar is deliberately the smallest one that serves the job this console exists for — A/B
// measuring a running frame — rather than a shell:
//
//   * `;` separates statements, so a whole configuration is one line and one keypress:
//     `foliage.enabled 0; objects.trees.enabled 0; sun.shadows.enabled 0`. Toggling three things with
//     three round trips through the input box is three different frames, which is the one thing a
//     measurement cannot afford.
//   * double quotes group words, because a value may contain a space and a summary certainly does.
//   * `//` ends the line, so a configuration can be pasted out of a note or a commit message with the
//     reasoning still attached to it.
//
// Everything else — variables, substitution, globbing — is absent on purpose: this parses the input of a
// person watching a frame time, and every construct that can surprise them costs more than it buys.
public static class ConsoleCommandLine
{
    // The statements on one line, in order, with blank ones dropped. A `;` inside quotes is a character
    // like any other, so a quoted value is never cut in half.
    public static List<string> Split(string line)
    {
        var statements = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (!quoted && c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                break; // a comment runs to the end of the line, so nothing after it is a statement
            if (c == '"')
                quoted = !quoted;
            if (c == ';' && !quoted)
            {
                Flush(statements, current);
                continue;
            }
            current.Append(c);
        }
        Flush(statements, current);
        return statements;
    }

    private static void Flush(List<string> statements, StringBuilder current)
    {
        string statement = current.ToString().Trim();
        current.Clear();
        if (statement.Length > 0)
            statements.Add(statement);
    }

    // The words of one statement. Quotes group, `\"` is a literal quote inside a quoted word, and an
    // unterminated quote takes the rest of the statement rather than failing: somebody is typing this
    // live, and refusing the line they are halfway through is worse than reading it the obvious way.
    public static List<string> Words(string statement)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        bool started = false; // an empty "" is a word; whitespace between words is not
        for (int i = 0; i < statement.Length; i++)
        {
            char c = statement[i];
            if (quoted && c == '\\' && i + 1 < statement.Length && statement[i + 1] == '"')
            {
                current.Append('"');
                i++;
                continue;
            }
            if (c == '"')
            {
                quoted = !quoted;
                started = true;
                continue;
            }
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (started)
                    words.Add(current.ToString());
                current.Clear();
                started = false;
                continue;
            }
            current.Append(c);
            started = true;
        }
        if (started)
            words.Add(current.ToString());
        return words;
    }
}
