using System;

namespace UnturnedGodot.Data;

// Converts asynchronous renderer observations into a deterministic settling decision. Requiring
// consecutive stable observations prevents a one-frame queue gap from being mistaken for completion.
public sealed class FoliageSettlingTracker
{
    private readonly int _requiredStableObservations;
    private int _stableObservations;

    public FoliageSettlingTracker(int requiredStableObservations)
    {
        if (requiredStableObservations <= 0)
            throw new ArgumentOutOfRangeException(nameof(requiredStableObservations));
        _requiredStableObservations = requiredStableObservations;
    }

    public bool Observe(bool foundRenderer, bool allSettled)
    {
        if (!foundRenderer)
            return true;
        if (!allSettled)
        {
            _stableObservations = 0;
            return false;
        }
        return ++_stableObservations >= _requiredStableObservations;
    }
}
