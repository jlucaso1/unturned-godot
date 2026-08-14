using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot;

// One collision shape placed into a physics body: which shape from the builder's pool, where it sits, and
// which surface it is made of.
//
// The surface rides HERE rather than on the shape, because shapes are shared. ObjectsBuilder pools one per
// (collider, scale) and its final dedup pass aliases identical dimensions across assets outright, so a
// single BoxShape3D can back both a wooden crate and a metal locker. The placement is the finest thing
// that still knows which collider it came from — and it is also what BodyAddShape consumes, in order, which
// makes its position the very index a raycast hands back.
//
// A byte rather than a string: there is one placement per placed collider on the map — hundreds of
// thousands on a large one — and the whole shipped game names 21 physics materials.
public readonly record struct CollisionPlacement(int Shape, Transform3D Transform, byte Material);

// The physics-material names one builder has seen, interned to a byte. Index 0 is the empty name, so a
// collider with no material needs no entry at all — the original treats that as "no impact effect" rather
// than as missing data. About one collider in five on a placed prefab is bare that way.
public sealed class SurfaceTable
{
    private readonly Dictionary<string, byte> _indices = new(StringComparer.Ordinal);
    private readonly List<string> _names = new();

    // The interned names, in index order. Entry 0 is surface index 1, since 0 means "none".
    public IReadOnlyList<string> Names => _names;

    public byte IndexOf(string materialName)
    {
        if (materialName.Length == 0)
            return 0;
        if (_indices.TryGetValue(materialName, out byte existing))
            return existing;
        // 255 distinct surfaces on one map is far beyond anything the content ships; a table that full
        // simply stops interning, and the extra names read back as "no material" — no sound and no mark,
        // which is exactly what a collider that never had one does.
        if (_names.Count >= byte.MaxValue)
            return 0;
        _names.Add(materialName);
        byte index = (byte)_names.Count;
        _indices[materialName] = index;
        return index;
    }

    // The name a surface byte refers to, or empty for 0 and for anything past the table. Shared by both
    // body flavours, so the two cannot disagree about what an index means.
    public static string NameAt(IReadOnlyList<string> names, byte index) =>
        index == 0 || index > names.Count ? string.Empty : names[index - 1];
}
