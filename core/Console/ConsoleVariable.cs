using System;
using System.Globalization;

namespace UnturnedGodot.DevConsole;

public enum ConsoleValueKind { Switch, Whole, Number }

// What a variable does once its value has changed, and what it wants to say about having done it.
//
// A null answer is the ordinary case: the change landed and the console prints the one-line confirmation
// on its own. A string is a NOTICE, not a failure — "there is no world loaded, so this is remembered for
// the one that loads next" is the shape of every message this returns. A binding that cannot fail has no
// reason to invent one.
public delegate string? ConsoleApply(ConsoleVariable variable);

// One tunable, addressed by a dotted name and carrying its own type, range and default.
//
// The value is held HERE rather than in the thing it controls, and that is the design decision the rest
// of the console rests on. A renderer, a light and a viewport are all torn down and rebuilt when the
// player returns to the menu and loads another map, so a toggle that lived in them would silently revert
// on every load — the measurement you set up would evaporate the moment you changed maps to check it
// against a second one. Holding the value here means the console can re-apply the whole configuration to
// a freshly built world (see ConsoleRegistry.Reapply), and `reset` has something authoritative to restore.
public sealed class ConsoleVariable
{
    private readonly ConsoleApply _apply;
    private readonly int[]? _allowed; // set only by Choice; null means the whole range is accepted
    private double _value;

    private ConsoleVariable(string name, string summary, ConsoleValueKind kind, double value,
        double minimum, double maximum, ConsoleApply apply, int[]? allowed = null)
    {
        _allowed = allowed;
        Name = name;
        Summary = summary;
        Kind = kind;
        DefaultValue = value;
        Minimum = minimum;
        Maximum = maximum;
        _value = value;
        _apply = apply;
    }

    public string Name { get; }
    public string Summary { get; }
    public ConsoleValueKind Kind { get; }
    public double Minimum { get; }
    public double Maximum { get; }
    public double DefaultValue { get; }

    public bool AsBool => _value != 0d;
    public int AsInt => (int)Math.Round(_value);
    public float AsFloat => (float)_value;
    public bool IsDefault => _value.Equals(DefaultValue);

    // A switch: 0/1, off/on, false/true, no/yes. Every rendering toggle is one of these, so the accepted
    // spellings are wide on purpose — the point is to type it without thinking while watching a graph.
    public static ConsoleVariable Switch(string name, string summary, bool value, ConsoleApply apply) =>
        new(name, summary, ConsoleValueKind.Switch, value ? 1d : 0d, 0d, 1d, apply);

    // A whole number inside a closed range: a mode index, a texture size, a frame cap.
    public static ConsoleVariable Whole(string name, string summary, int value, int minimum, int maximum,
        ConsoleApply apply) =>
        new(name, summary, ConsoleValueKind.Whole, value, minimum, maximum, apply);

    // A whole number drawn from a discrete set rather than a range: shadow cascade counts are 1, 2 or 4,
    // and nothing sensible happens at 3.
    //
    // Without this the range is the only guard, and a gap inside it lands on whatever the binding's switch
    // falls through to — the console would then echo and remember a 3 while the renderer ran 2, which is
    // the same "what you typed is not what you measured" failure the refusal below exists to prevent. It
    // is worse than a clamp, in fact, because the transcript records the number that was never applied.
    public static ConsoleVariable Choice(string name, string summary, int value, int[] allowed,
        ConsoleApply apply)
    {
        if (allowed.Length == 0)
            throw new ArgumentException("A choice needs at least one allowed value.", nameof(allowed));
        int minimum = allowed[0];
        int maximum = allowed[0];
        foreach (int option in allowed)
        {
            minimum = Math.Min(minimum, option);
            maximum = Math.Max(maximum, option);
        }
        return new ConsoleVariable(name, summary, ConsoleValueKind.Whole, value, minimum, maximum, apply,
            (int[])allowed.Clone());
    }

    // A real number inside a closed range: a resolution scale, a distance, a screen-space error.
    public static ConsoleVariable Number(string name, string summary, float value, float minimum,
        float maximum, ConsoleApply apply) =>
        new(name, summary, ConsoleValueKind.Number, value, minimum, maximum, apply);

    // The current value as it is typed and printed. A switch reads back as 0/1 whichever spelling set it,
    // so `list` output can be pasted straight back in.
    public string Value => Format(_value);

    public string DefaultText => Format(DefaultValue);

    private string Format(double value) => Kind == ConsoleValueKind.Number
        ? value.ToString("0.###", CultureInfo.InvariantCulture)
        : ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);

    private string AllowedText => _allowed == null ? "" : string.Join("/", _allowed);

    // "foliage.enabled = 0  (default 1)" / "r.scale = 1  (default 1, 0.25..2)". The range is printed for
    // everything that has one worth reading — a switch's 0..1 is not, and a choice prints the values it
    // takes rather than the span they happen to cover, because that span contains numbers it refuses.
    public string Describe() => Kind switch
    {
        ConsoleValueKind.Switch => $"{Name} = {Value}  (default {DefaultText})",
        _ when _allowed != null => $"{Name} = {Value}  (default {DefaultText}, one of {AllowedText})",
        _ => $"{Name} = {Value}  (default {DefaultText}, {Format(Minimum)}..{Format(Maximum)})",
    };

    // Parses `text` and stores it, leaving the variable untouched when it cannot. Out-of-range values are
    // REFUSED rather than clamped: a clamp answers `r.scale 5` with a frame that looks exactly like the
    // one before it, and the whole point of this console is that what you typed is what you measured.
    public bool TrySet(string text, out string failure)
    {
        if (!TryParse(text, out double parsed))
        {
            failure = Kind switch
            {
                ConsoleValueKind.Switch => $"'{text}' is not a switch: use 0/1, off/on, false/true or no/yes.",
                ConsoleValueKind.Whole => $"'{text}' is not a whole number.",
                _ => $"'{text}' is not a number.",
            };
            return false;
        }
        if (parsed < Minimum || parsed > Maximum)
        {
            failure = $"{Format(parsed)} is outside {Name}'s range "
                + $"({Format(Minimum)}..{Format(Maximum)}).";
            return false;
        }
        if (_allowed != null && Array.IndexOf(_allowed, (int)Math.Round(parsed)) < 0)
        {
            failure = $"{Format(parsed)} is not one of {Name}'s values ({AllowedText}).";
            return false;
        }
        _value = parsed;
        failure = "";
        return true;
    }

    private bool TryParse(string text, out double value)
    {
        value = 0d;
        if (Kind == ConsoleValueKind.Switch)
        {
            switch (text.Trim().ToLowerInvariant())
            {
                case "1": case "on": case "true": case "yes": value = 1d; return true;
                case "0": case "off": case "false": case "no": return true;
                default: return false;
            }
        }
        if (Kind == ConsoleValueKind.Whole)
        {
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole))
                return false;
            value = whole;
            return true;
        }
        // Finite only. float.TryParse happily accepts "NaN" and "Infinity", and either one poisons the
        // viewport property it is written into for the rest of the session.
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            || !double.IsFinite(number))
            return false;
        value = number;
        return true;
    }

    public void SetToDefault() => _value = DefaultValue;

    // Hands the current value to whatever it drives. Returns the binding's notice, if it had one.
    public string? Apply() => _apply(this);
}
