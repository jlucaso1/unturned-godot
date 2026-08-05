using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace UnturnedGodot.Dat;

// Faithful subset of Unturned's DatParser node model (UnturnedDat/DatDictionary.cs et al).
public abstract class DatNode { }

public sealed class DatValue : DatNode
{
    public readonly string Value;
    public DatValue(string value) => Value = value;
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

    public bool TryGetString(string key, out string value)
    {
        if (_nodes.TryGetValue(key, out DatNode? node) && node is DatValue v)
        {
            value = v.Value;
            return true;
        }
        value = string.Empty;
        return false;
    }

    public string? GetString(string key) => TryGetString(key, out string v) ? v : null;

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

    public bool TryGetGuid(string key, out Guid guid)
    {
        // Unturned treats empty/"0" as a null GUID (Guid.Empty).
        if (TryGetString(key, out string s) && Guid.TryParse(s, out guid))
            return guid != Guid.Empty;
        guid = Guid.Empty;
        return false;
    }

    public bool TryGetUInt16(string key, out ushort value) =>
        ushort.TryParse(GetString(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    public bool TryGetInt32(string key, out int value) =>
        int.TryParse(GetString(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    // Unturned writes its .dat floats with an invariant decimal point, so the parse is culture-fixed too.
    public bool TryGetSingle(string key, out float value) =>
        float.TryParse(GetString(key), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    // ParseUInt8. Separate from the 16-bit reader because the fields that use it — a blade id, a rewards
    // count — are genuinely bytes, and reading one into a ushort would silently accept 300.
    public bool TryGetByte(string key, out byte value) =>
        byte.TryParse(GetString(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    // ParseBool. Unturned's .dats carry booleans two ways: spelled out ("Causes_Fall_Damage False") and
    // by bare presence ("No_Debris"), and its own reader accepts both — a key whose value does not parse
    // as a bool is still a key that is there, which is what a bare flag is. `defaultValue` is what an
    // ABSENT key means, and it is not always false: several fields default to true and are turned off by
    // writing the word.
    public bool GetBool(string key, bool defaultValue = false)
    {
        if (!TryGetString(key, out string raw))
            return defaultValue;
        return !bool.TryParse(raw, out bool value) || value;
    }

    public bool ContainsKey(string key) => _nodes.ContainsKey(key);
}
