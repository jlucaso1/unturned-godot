using Godot;

namespace UnturnedGodot.Player;

// Where the first-person rig has to sit for the player to be looking out of its own head.
//
// Unturned parents the first-person camera UNDER the viewmodel skeleton's head: PlayerAnimator resolves
// it as firstSkeleton/Spine/Skull/ViewmodelCamera, so the eye rides whatever the animation does to the
// skull. That parenting is the whole reason the arms stay framed the same way in every stance — the
// stance clips (Idle_Crouch, Idle_Prone and the moving pairs) pose the entire skeleton, and the eye is
// carried along by the same pose rather than staying behind at some fixed height.
//
// This port hangs the rig off the camera instead of the camera off the rig, because the camera's
// rotation is PlayerLook's and a bone may not own it. The same relationship therefore has to be
// expressed inverted: slide the rig by the negation of wherever the pose has left the eye bone, which
// puts that bone back on the camera. It is the POSED position, not the bind rest — a rest offset agrees
// with the pose only while the character stands in its bind attitude, and is wrong by the drop of a
// crouch or the length of a prone body in every other stance.
//
// Only the translation is taken. The rotation of Unturned's eye frame is authored on the ViewmodelCamera
// child, which belongs to the arms rig this pipeline cannot decode yet (CharacterModel.ViewmodelFromBody);
// composing that authored rotation with a different rig's skull mixes two frames and lands most of a
// right angle away from the view.
public static class ViewmodelAnchor
{
    // `eyeBone` is the eye bone's origin in the rig's own space, as the CURRENT pose leaves it.
    // `nudge` is the operator's manual offset (UG_VIEWMODEL_OFFSET), zero in play.
    public static Vector3 RigPosition(Vector3 eyeBone, Vector3 nudge) => nudge - eyeBone;
}
