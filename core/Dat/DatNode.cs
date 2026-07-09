using System;
using System.Collections.Generic;
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

    public bool TryGetDictionary(string key, out DatDictionary dict)
    {
        if (_nodes.TryGetValue(key, out DatNode? node) && node is DatDictionary d)
        {
            dict = d;
            return true;
        }
        dict = null!;
        return false;
    }

    public bool TryGetList(string key, out DatList list)
    {
        if (_nodes.TryGetValue(key, out DatNode? node) && node is DatList l)
        {
            list = l;
            return true;
        }
        list = null!;
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
}
