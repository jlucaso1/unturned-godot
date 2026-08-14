using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace UnturnedGodot.Data;

// Ports LevelNavigation.load: the navmesh baking bounds (Environment/Bounds.dat — each stored center/size
// already includes the BOUNDS_SIZE = 64 m expansion) and their per-flag data (Environment/Flags_Data.dat:
// maxZombies etc.). Zombie spawn points and player positions map to a bound by XZ containment, exactly
// like tryGetBounds; the navmesh surface test (checkNavigation) has no counterpart here — spawn points
// inside the bound are all accepted, a documented approximation.
public sealed class NavBound
{
    public Vector3 Center;
    public Vector3 Size;
    public byte MaxZombies = 64;
    public bool SpawnZombies = true;
    public bool HyperAgro;

    public bool ContainsXZ(Vector3 point) =>
        Mathf.Abs(point.X - Center.X) <= Size.X * 0.5f &&
        Mathf.Abs(point.Z - Center.Z) <= Size.Z * 0.5f;
}

public static class LevelNavigationData
{
    // "Nowhere": a point outside every bound. It is a real byte value, so it also names a legal INDEX,
    // and the two must never be the same thing — region 255 aliasing with "no region" would put a
    // player standing in the 256th bound into the same bucket as one standing off the map, and would
    // index ZombieHost's per-bound array out of range.
    public const byte NoBound = byte.MaxValue;

    // How many bounds a level may have, therefore: indices 0 through 254.
    //
    // Bounds.dat counts its entries in a byte, so a file cannot express more than 255 and the collision
    // is out of reach through Load today. That is a property of the file format rather than of this
    // code, and it is exactly the kind that a workshop map, a new format version or a synthesised level
    // stops guaranteeing without anything here noticing.
    public const int MaxBounds = NoBound;

    public static List<NavBound> Load(string environmentDir)
    {
        var bounds = new List<NavBound>();

        string boundsPath = Path.Combine(environmentDir, "Bounds.dat");
        if (File.Exists(boundsPath))
        {
            using var river = new River(boundsPath);
            byte version = river.ReadByte();
            if (version > 0)
            {
                byte count = river.ReadByte();
                for (int i = 0; i < count && bounds.Count < MaxBounds; i++)
                {
                    Vector3 center = river.ReadSingleVector3();
                    // The file stores Unity coordinates; containment runs against Godot positions,
                    // so the center crosses the world's Z-mirror here (sizes are mirror-invariant).
                    center.Z = -center.Z;
                    bounds.Add(new NavBound { Center = center, Size = river.ReadSingleVector3() });
                }
            }
        }

        string flagsPath = Path.Combine(environmentDir, "Flags_Data.dat");
        if (File.Exists(flagsPath))
        {
            using var river = new River(flagsPath);
            byte version = river.ReadByte();
            if (version > 0)
            {
                byte count = river.ReadByte();
                for (int i = 0; i < count && i < bounds.Count; i++)
                {
                    river.ReadString(); // difficulty GUID
                    if (version > 1)
                        bounds[i].MaxZombies = river.ReadByte();
                    if (version > 2)
                        bounds[i].SpawnZombies = river.ReadBoolean();
                    if (version >= 4)
                        bounds[i].HyperAgro = river.ReadBoolean();
                    if (version >= 5)
                        river.ReadInt32(); // maxBossZombies (bosses are out of scope)
                }
            }
        }

        return bounds;
    }

    // LevelNavigation.tryGetBounds: the first bound whose XZ footprint contains the point.
    //
    // Never scans past MaxBounds, so the index it returns can never BE NoBound. Load truncates to the
    // same ceiling, but this takes any list — the repro harness, a test, a future loader — and the one
    // thing that must hold everywhere is that "the region you are in" and "you are in no region" are
    // different answers.
    public static byte TryGetBound(IReadOnlyList<NavBound> bounds, Vector3 point)
    {
        int scan = Math.Min(bounds.Count, MaxBounds);
        for (int i = 0; i < scan; i++)
            if (bounds[i].ContainsXZ(point))
                return (byte)i;
        return NoBound;
    }
}
