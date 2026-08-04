namespace UnturnedGodot.Player;

// Arbitrates a one-shot gesture against the looping movement state, which is what Unturned's animator
// does implicitly: PlayerAnimator.play lays a clip over whatever updateState last chose and clears the
// gesture, and the movement clip comes back once the overlay is done.
//
// The port needs it spelled out, because the controller pushes the movement state every tick and would
// otherwise cut a swing off on the frame after it started. Kept here, apart from the skeleton, because
// the rule is about time and requests rather than about bones — so it is unit-tested, and so every
// animated character (the player today, an NPC or a zombie's non-looping reactions later) arbitrates the
// same way.
public sealed class GestureOverlay
{
    private float _remaining;
    private EPlayerStance _stance;
    private bool _moving;

    // True while a gesture owns the rig.
    public bool IsPlaying => _remaining > 0f;

    // Starts a gesture of `length` seconds. `stance`/`moving` are where the body goes back to when it
    // ends. Re-triggering an already-playing gesture (a second punch inside the first) keeps the
    // ORIGINAL fallback: the state to return to is the one the movement was in before any of this, not
    // the frozen mid-swing pose the second trigger happened to interrupt.
    public void Begin(float length, EPlayerStance stance, bool moving)
    {
        if (length <= 0f)
            return;
        if (!IsPlaying)
        {
            _stance = stance;
            _moving = moving;
        }
        _remaining = length;
    }

    // A movement-state request. Returns true when the caller should apply it now, false when a gesture
    // owns the rig — in which case it is remembered and applied when the gesture ends, so a player who
    // crouches mid-swing stands out of it into the crouch clip rather than the one they left.
    public bool Request(EPlayerStance stance, bool moving)
    {
        if (!IsPlaying)
            return true;
        _stance = stance;
        _moving = moving;
        return false;
    }

    // Advances the clock. Returns the state to restore on the frame the gesture finishes, and null on
    // every other frame — including while nothing is playing.
    public (EPlayerStance Stance, bool Moving)? Advance(float delta)
    {
        if (!IsPlaying)
            return null;
        _remaining -= delta;
        return IsPlaying ? null : (_stance, _moving);
    }

    // Drops the gesture without restoring anything: the caller is deliberately changing clip, so it
    // already knows what should play instead.
    public void Cancel() => _remaining = 0f;
}
