using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace UnturnedGodot.Dat;

// Faithful subset of Unturned's DatParser node model (UnturnedDat/DatDictionary.cs et al).
public abstract class DatNode { }

public sealed class DatValue : DatNode
{
    // Null, not "", is what the game stores for a key written with no value at all:
    // DatParser.ReadDictionaryValue builds `new DatValue(maybeValueToken.type == Value ? ... : null)`
    // (DatParser.cs:267).
    //
    // That null is spelled here as an empty Value plus IsNull rather than as a null reference, so the
    // field stays non-null for anything that indexes into it while the accessors below still report the
    // game's null. The two can only ever differ for a bare flag — every list item and every inline
    // value comes from a real token — and no accessor exposes the difference except GetString, which is
    // exactly where the game exposes it too.
    public readonly string Value;

    // True for a key written with no value. DatDictionary.TryGetString reports it as the null that
    // DatDictionaryEx.TryGetString hands back for one.
    public readonly bool IsNull;

    public DatValue(string? value)
    {
        IsNull = value is null;
        Value = value ?? string.Empty;
    }
}

public sealed class DatList : DatNode
{
    public readonly List<DatNode> Items = new();
}

public sealed class DatDictionary : DatNode
{
    // Unturned uses OrdinalIgnoreCase for keys; matching that keeps lookups compatible.
    private readonly Dictionary<string, DatNode> _nodes =
        new(StringComparer.OrdinalIgnoreCase);

    // Last value wins on duplicate keys, mirroring AddValueToDictionary.
    public void Set(string key, DatNode node) => _nodes[key] = node;

    public IEnumerable<string> Keys => _nodes.Keys;

    // DatDictionaryEx.TryGetString: true when the key holds a value node, handing back that node's
    // string — which is null for a key written with no value.
    public bool TryGetString(string key, out string? value)
    {
        if (_nodes.TryGetValue(key, out DatNode? node) && node is DatValue v)
        {
            value = v.IsNull ? null : v.Value;
            return true;
        }
        value = null;
        return false;
    }

    public string? GetString(string key) => TryGetString(key, out string? v) ? v : null;

    public bool TryGetDictionary(string key, [MaybeNullWhen(false)] out DatDictionary dict)
    {
        if (_nodes.TryGetValue(key, out DatNode? node) && node is DatDictionary d)
        {
            dict = d;
            return true;
        }
        dict = null;
        return false;
    }

    public bool TryGetList(string key, [MaybeNullWhen(false)] out DatList list)
    {
        if (_nodes.TryGetValue(key, out DatNode? node) && node is DatList l)
        {
            list = l;
            return true;
        }
        list = null;
        return false;
    }

    // DatValueEx.TryParseGuid is Guid.TryParse and nothing else: an all-zero GUID is well formed, so it
    // parses, and the game's ParseGuid hands back Guid.Empty successfully. Rejecting it here would make
    // an asset that writes one out unreadable in a way the game does not.
    public bool TryGetGuid(string key, out Guid guid) =>
        Guid.TryParse(GetString(key), out guid);

    // Every numeric accessor in DatValueEx uses NumberStyles.Any with InvariantCulture. `Any` is much
    // wider than Integer/Float — it adds AllowDecimalPoint, AllowThousands, AllowParentheses and
    // AllowCurrencySymbol — and the width is load-bearing, because a field that fails to parse silently
    // takes the caller's default instead. `Health 200.0` is 200 to the game and would be 0 here under
    // NumberStyles.Integer; `(5)` is -5 there and 0 here.
    private const NumberStyles Styles = NumberStyles.Any;

    public bool TryGetUInt16(string key, out ushort value) =>
        ushort.TryParse(GetString(key), Styles, CultureInfo.InvariantCulture, out value);

    public bool TryGetInt32(string key, out int value) =>
        int.TryParse(GetString(key), Styles, CultureInfo.InvariantCulture, out value);

    // Unturned writes its .dat floats with an invariant decimal point, so the parse is culture-fixed too.
    public bool TryGetSingle(string key, out float value) =>
        float.TryParse(GetString(key), Styles, CultureInfo.InvariantCulture, out value);

    // ParseUInt8. Separate from the 16-bit reader because the fields that use it — a blade id, a rewards
    // count — are genuinely bytes, and reading one into a ushort would silently accept 300.
    public bool TryGetByte(string key, out byte value) =>
        byte.TryParse(GetString(key), Styles, CultureInfo.InvariantCulture, out value);

    // DatValueEx.TryParseBool (:134-160), which is not bool.TryParse: a ONE-character value is read as
    // a letter — 'y'/'t'/'1' true, 'n'/'f'/'0' false, anything else a failure without consulting
    // bool.TryParse at all — and only a longer value goes through bool.TryParse.
    //
    // The part that matters most is what it does with a value that is null, empty, or unparseable: it
    // returns FALSE, so ParseBool hands back the caller's default. A bare flag ("No_Debris") parses as
    // a key holding DatValue(null), so ParseBool on one yields the default rather than true — and that
    // is precisely why the game reads bare flags with ContainsKey and never with ParseBool
    // (ObjectAsset.cs:1154, 1165-1167; ResourceAsset.cs:483, 506). `defaultValue` is what an ABSENT key
    // means, and it is not always false: several fields default to true and are turned off by writing
    // the word (ResourceAsset.cs:479, ObjectAsset.cs:1132).
    public bool GetBool(string key, bool defaultValue = false) =>
        TryGetBool(key, out bool value) ? value : defaultValue;

    public bool TryGetBool(string key, out bool value)
    {
        value = default;
        if (!TryGetString(key, out string? raw) || string.IsNullOrEmpty(raw))
            return false;

        if (raw.Length == 1)
        {
            char letter = raw[0];
            if (letter is 'y' or 't' or '1')
            {
                value = true;
                return true;
            }
            if (letter is 'n' or 'f' or '0')
            {
                value = false;
                return true;
            }
            // A single character that is none of those never reaches bool.TryParse in the game: the
            // branch it falls out of is the `else` that would have called it.
            return false;
        }

        return bool.TryParse(raw, out value);
    }

    public bool ContainsKey(string key) => _nodes.ContainsKey(key);
}
