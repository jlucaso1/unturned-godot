using System;
using System.Collections.Generic;

namespace UnturnedGodot.Zombies;

// Ports ZombieManager.ZombieSpecialityWeightedRandom (ZombieManager.cs:951-1036) exactly: entries are
// inserted sorted by DESCENDING weight, and one draw walks that order subtracting as it goes.
//
// The order matters, and not only cosmetically. `get()` scales one uniform draw by the total weight and
// then walks the list; which entry a given draw lands on therefore depends on the sequence, so
// reproducing the sort is what makes a seeded roll here land where a seeded roll there would. The
// distribution alone would survive any order, but this port's whole point is that it does not have to
// guess which of those two properties someone downstream is relying on.
public sealed class ZombieSpecialityWeights : IComparer<ZombieSpecialityWeights.Entry>
{
    public readonly record struct Entry(EZombieSpeciality Value, float Weight);

    private readonly List<Entry> _entries = new();

    public float TotalWeight { get; private set; }

    public IReadOnlyList<Entry> Entries => _entries;

    public void Clear()
    {
        _entries.Clear();
        TotalWeight = 0f;
    }

    // "weight = Mathf.Max(weight, 0.0f)" then a BinarySearch insert under this comparer. A negative
    // weight in a hand-edited asset is clamped rather than allowed to eat the total.
    public void Add(EZombieSpeciality value, float weight)
    {
        weight = MathF.Max(weight, 0f);
        var entry = new Entry(value, weight);
        int index = _entries.BinarySearch(entry, this);
        if (index < 0)
            index = ~index;
        _entries.Insert(index, entry);
        TotalWeight += weight;
    }

    // "Not ideal, but many configurations exist assuming normal is the default with all chances adding
    // up to 100%." NORMAL takes whatever weight the specialities left unclaimed — which is why an asset
    // whose weights sum past 1 produces no normal zombies at all rather than an error.
    public void AddNormalRemainder() => Add(EZombieSpeciality.Normal, 1f - TotalWeight);

    // "Default CompareTo uses less than, so we negate to put highest weights at the front of the list."
    public int Compare(Entry lhs, Entry rhs) => -lhs.Weight.CompareTo(rhs.Weight);

    // The draw. `unitRandom` is Unity's Random.value — uniform over [0, 1).
    public EZombieSpeciality Pick(float unitRandom)
    {
        if (_entries.Count < 1)
            return default; // "List is empty."

        float random = unitRandom * TotalWeight;
        foreach (Entry entry in _entries)
        {
            if (random < entry.Weight)
                return entry.Value;
            // e.g. [0] is 10, [1] is 5, and random is 12 -> subtract 10 so random is 2 and selects [1].
            random -= entry.Weight;
        }

        // "Maybe edge case with small numbers at end of list? Default to highest weight."
        return _entries[0].Value;
    }

    public EZombieSpeciality Pick(Random random) => Pick(random.NextSingle());
}
