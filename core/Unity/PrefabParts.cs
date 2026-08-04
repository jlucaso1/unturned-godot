using System;
using System.Collections.Generic;

namespace UnturnedGodot.Unity;

// Which detail level one renderable child of a prefab belongs to.
public enum EPrefabLevel
{
    Lod0,       // full detail
    Lod1,       // the authored lower level
    Always,     // drawn whatever the distance (a vehicle's seats, lights, sirens)
    Unlevelled, // carries no level in its name; drawn only when the prefab authored no levels at all
    Excluded,   // never drawn
}

// The rules that decide which of a prefab's renderable children Unturned actually draws. Kept apart from
// the SerializedFile walk that feeds them so they can be tested on their own.
public static class PrefabParts
{
    // Unturned keeps a destructible object's alternate states inside the one prefab: "Alive" is the model a
    // placed object shows, "Dead" the wreck it becomes when its health runs out, and "Ragdoll" the debris it
    // throws while falling apart. LevelObject enables exactly one of the three, and a level that has just
    // loaded is never in the destroyed state — so everything under Dead or Ragdoll is authored geometry the
    // game does not draw, and reading it as ordinary parts is wrong in two different ways: where the state
    // nodes carry their own Model_0 (Computer #1, Transit #1) the debris is drawn ON TOP of the live model,
    // and where they carry the mesh directly (Camera #1, Bench #1) the destroyed model can be picked
    // INSTEAD of the live one, since which of them the reader meets first is just file order.
    public static bool IsHiddenState(string gameObjectName) =>
        gameObjectName is "Dead" or "Ragdoll";

    // Unturned names a levelled renderer "<something>_<n>" — Model_0/Model_1 overwhelmingly — and the port
    // reads that suffix rather than the LODGroup component the editor also writes.
    //
    // A vehicle is the one prefab where that reading is wrong: VehicleAsset addresses a vehicle's parts by
    // name (Seats/Seat_#, Headlights, Sirens), so Seat_0 and Seat_1 are the driver's and passenger's seat,
    // not two levels of one seat. Take only the Model_<n> chain as levels there; everything else is a part
    // of the vehicle that is always drawn.
    public static EPrefabLevel LevelOf(string partName, bool isVehicle)
    {
        if (!isVehicle)
        {
            if (partName.EndsWith("_0", StringComparison.Ordinal))
                return EPrefabLevel.Lod0;
            return partName.EndsWith("_1", StringComparison.Ordinal)
                ? EPrefabLevel.Lod1
                : EPrefabLevel.Unlevelled;
        }

        if (partName.StartsWith("Model_", StringComparison.Ordinal))
        {
            return partName switch
            {
                "Model_0" => EPrefabLevel.Lod0,
                "Model_1" => EPrefabLevel.Lod1,
                _ => EPrefabLevel.Excluded, // Model_2 and below: levels this port does not carry
            };
        }

        // A depth mask writes depth only in Unity, hiding what is behind it rather than drawing itself;
        // rendered as ordinary geometry it would be a solid slab through the vehicle.
        return partName is "DepthMask" ? EPrefabLevel.Excluded : EPrefabLevel.Always;
    }
}

// Collects one bundle's renderable parts, keyed by prefab, and answers what to draw at each level. The part
// type is the caller's (a mesh part, a collider), so the same grouping serves both.
public sealed class PrefabPartSet<TPart>
{
    private readonly Dictionary<string, List<TPart>> _lod0 = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TPart>> _lod1 = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TPart>> _always = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TPart>> _unlevelled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TPart> _first = new(StringComparer.Ordinal);

    // `partName` is the name of the GameObject the renderer sits on. Parts that are never drawn are
    // dropped here rather than by the caller, so they cannot be picked up as a fallback either.
    public void Add(string key, string partName, bool isVehicle, TPart part)
    {
        switch (PrefabParts.LevelOf(partName, isVehicle))
        {
            case EPrefabLevel.Lod0:
                Append(_lod0, key, part);
                break;
            case EPrefabLevel.Lod1:
                Append(_lod1, key, part);
                break;
            case EPrefabLevel.Always:
                // Also held aside: a prefab with no authored lower level must not gain one made of nothing
                // but its seats and lights, which would then replace the whole vehicle at distance.
                Append(_lod0, key, part);
                Append(_always, key, part);
                break;
            case EPrefabLevel.Unlevelled:
                Append(_unlevelled, key, part);
                break;
            default:
                return;
        }
        _first.TryAdd(key, part);
    }

    // The parts to draw up close, and — for the prefabs that authored one — the set that replaces them at
    // distance. A prefab whose children carry no level names is NOT reduced to one of them: every part it
    // has is drawn, which is what Unity does with the prefab and what keeps a multi-part model whole.
    public (Dictionary<string, List<TPart>> Base, Dictionary<string, List<TPart>> Lower) Resolve()
    {
        var baseLevel = new Dictionary<string, List<TPart>>(StringComparer.Ordinal);
        var lower = new Dictionary<string, List<TPart>>(StringComparer.Ordinal);

        foreach (KeyValuePair<string, TPart> entry in _first)
        {
            string key = entry.Key;
            if (_lod0.TryGetValue(key, out List<TPart>? authored))
            {
                baseLevel[key] = authored;
                // A lower level is only meaningful against an authored LOD-0 set.
                if (!_lod1.TryGetValue(key, out List<TPart>? low))
                    continue;
                if (_always.TryGetValue(key, out List<TPart>? unlevelled))
                    low.AddRange(unlevelled); // the parts drawn at every distance, this level included
                lower[key] = low;
                continue;
            }

            // Nothing levelled: draw the unlevelled parts, or — for a prefab that has only lower levels —
            // the single part that stands in for it, as before.
            baseLevel[key] = _unlevelled.TryGetValue(key, out List<TPart>? parts)
                ? parts
                : new List<TPart> { entry.Value };
        }

        return (baseLevel, lower);
    }

    private static void Append(Dictionary<string, List<TPart>> byKey, string key, TPart part)
    {
        if (!byKey.TryGetValue(key, out List<TPart>? list))
            byKey[key] = list = new List<TPart>();
        list.Add(part);
    }
}
