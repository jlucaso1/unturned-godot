namespace UnturnedGodot.Player;

// Which movement state the FIRST-PERSON rig plays, which is not always the one the player is in.
//
// PlayerAnimator drives the two rigs differently, and only here:
//
//     if (firstAnimator.getAnimationPlaying()) firstAnimator.state("Idle_Stand");
//     else                                     updateState(firstAnimator);
//
//     updateState(thirdAnimator);   // the body always plays the real stance
//
// It is not cosmetic. A gesture is mixed onto the shoulders alone (CharacterAnimator.mixAnimation), so
// where the rest of the arm ends up comes from the state clip underneath it — and the crouch and prone
// idles fold the body down and forward. A punch thrown from one of those swings out of the view
// entirely: the animation plays, the arm is simply not on screen. Standing the arms up for the length of
// the gesture is what the original does about it.
//
// The third-person body keeps its real stance, because there the whole character is visible and the
// crouch is the point.
public static class ViewmodelState
{
    // The stance and movement the arms should play. `gesturePlaying` is CharacterAnimator's
    // getAnimationPlaying: a one-shot is on the rig right now.
    //
    // Standing and STILL, not standing and moving: `state("Idle_Stand")` names one clip, so the walk bob
    // stops for the length of the swing as well.
    public static (EPlayerStance Stance, bool Moving) For(EPlayerStance stance, bool moving,
        bool gesturePlaying) =>
        gesturePlaying ? (EPlayerStance.Stand, false) : (stance, moving);
}
