using System;

namespace UnturnedGodot;

// The name every object collision body carries, and the way back out of it.
//
// A body holds one asset's placements — all the pine trunks in a cell, all the garbage piles — so its
// name is that asset's GUID, optionally with the cell it covers. That name is the only identity a
// physics query can recover: the shapes live on a server-owned body by RID, and the hit reports the body
// and a shape index, not the placement that produced it.
//
// One place for both halves so the writer and the reader cannot drift, which is exactly the failure that
// would be invisible — a punch would simply stop damaging anything and look like a targeting bug.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class ObjectCollisionNames
{
    private const string Prefix = "Col_";

    public static string For(Guid guid) => $"{Prefix}{guid:N}";

    public static string For(Guid guid, int cellX, int cellZ) => $"{Prefix}{guid:N}_{cellX}_{cellZ}";

    // The asset a collision body belongs to, or Guid.Empty for anything that is not one of ours —
    // terrain, a ladder volume, a body some other system named.
    public static bool TryParseGuid(string name, out Guid guid)
    {
        guid = Guid.Empty;
        if (name == null || !name.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        ReadOnlySpan<char> rest = name.AsSpan(Prefix.Length);
        int end = rest.IndexOf('_');
        if (end >= 0)
            rest = rest[..end];
        return Guid.TryParseExact(rest, "N", out guid);
    }
}
