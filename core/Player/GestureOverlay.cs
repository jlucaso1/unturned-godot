namespace UnturnedGodot.Player;

// A one-shot clip laid OVER the looping movement state, which is what Unturned's animator does with a
// gesture: `PlayerAnimator.play` calls `CharacterAnimator.play`, and every gesture clip has been put on
// `AnimationState.layer = 1` by `mixAnimation` — usually with mixing transforms restricting it to an arm.
// The movement clip stays on layer 0 and keeps running the whole time; `updateState` goes on pushing it
// every frame, unaffected.
//
// So this is a second playhead, not an owner of the rig. It carries the clip, its own clock and the mask
// of bones it is allowed to write; the skeleton samples both layers each frame and lets the masked bones
// come from here. That the movement layer never stops is the whole point: a punch thrown while sprinting
// swings an arm, and the legs keep sprinting.
//
// Kept here, apart from the skeleton, because the rule is about time and masks rather than about bone
// transforms — so it is unit-tested, and so every animated character (the player today, an NPC's or a
// zombie's non-looping reactions later) overlays the same way.
public sealed class GestureOverlay
{
    private string _clip = "";
    private float _time;
    private float _length;
    private BoneMask? _mask;

    // True while a gesture is laid over the movement state.
    public bool IsPlaying => _clip.Length > 0;

    // The clip being overlaid, or "" when none is.
    public string Clip => _clip;

    // How far into that clip the overlay is. It does not wrap: a gesture plays exactly once, and the
    // sampler clamps past the last key.
    public float Time => _time;

    // Which bones the overlay may write, for the skeleton's per-frame merge. Null is the whole body.
    public BoneMask? Mask => _mask;

    // Starts `clip`, `length` seconds long, restricted to `mask` — null being Unturned's
    // `mixAnimation(name)` with no mixing transforms, i.e. a gesture that covers the whole body.
    //
    // A clip with no length is refused: it would be an overlay that never ends. Re-triggering while one
    // is already playing simply replaces it, which is what `CharacterAnimator.play` does — it stops the
    // gesture clip it was last given before playing the new one — and is how a second punch inside the
    // first swings again instead of being swallowed.
    public bool Begin(string clip, float length, BoneMask? mask)
    {
        if (length <= 0f || clip.Length == 0)
            return false;
        _clip = clip;
        _length = length;
        _mask = mask;
        _time = 0f;
        return true;
    }

    // Advances the clock, ending the overlay once the clip has run out. Returns true on the frame it
    // ended, and false on every other — including while nothing is playing.
    //
    // Nothing is handed back when it ends, unlike the arrangement this replaced: the movement layer never
    // stopped, so there is no state to restore. The bones the mask covered simply take its pose again.
    public bool Advance(float delta)
    {
        if (!IsPlaying)
            return false;
        _time += delta;
        if (_time < _length)
            return false;
        Cancel();
        return true;
    }

    // Drops the gesture: the movement layer underneath is already correct, so there is nothing else to do.
    public void Cancel()
    {
        _clip = "";
        _time = 0f;
        _length = 0f;
        _mask = null;
    }
}
