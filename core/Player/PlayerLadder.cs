using Godot;

namespace UnturnedGodot.Player;

// What a climb probe met: the ray hit, plus the frame of the ladder collider it hit.
//
// The frame is the ladder GameObject's own transform, exactly as Unturned's ladder code reads it
// (`ladder.collider.transform`). A ladder is authored lying along its local Z with its local Y pointing
// out of the wall, so its UP axis is the direction it faces, not the direction it is climbed — every test
// below is written against that, because the game's are.
public readonly struct LadderContact
{
    public readonly Vector3 Point;      // where the probe met the ladder
    public readonly Vector3 Normal;     // the surface normal there
    public readonly Transform3D Frame;  // the ladder collider's transform

    public LadderContact(Vector3 point, Vector3 normal, Transform3D frame)
    {
        Point = point;
        Normal = normal;
        Frame = frame;
    }

    // Normalized because a placed object may carry scale, and Unity's transform.up never does.
    public Vector3 Up => Frame.Basis.Y.Normalized();
    public Vector3 Origin => Frame.Origin;
}

// Whether a mount is refused for the state the player is in.
public enum EClimbTransition
{
    None,      // not climbing, and nothing to climb
    Mount,     // step onto the ladder at MountPoint
    Hold,      // already climbing and still on it: crouch/prone/sprint do not apply this tick
    Dismount,  // was climbing, the ladder is gone: back to standing
}

public readonly struct ClimbDecision
{
    public readonly EClimbTransition Transition;
    public readonly Vector3 MountPoint; // only meaningful for Mount

    public ClimbDecision(EClimbTransition transition, Vector3 mountPoint = default)
    {
        Transition = transition;
        MountPoint = mountPoint;
    }

    // True while the player is on a ladder this tick, which is what suppresses the ordinary stance intents.
    public bool IsClimbing => Transition is EClimbTransition.Mount or EClimbTransition.Hold;
}

// True when a standing capsule swept from `from` to `to` would hit something (RayMasks.BLOCK_LADDER).
public delegate bool CapsuleSweepBlocked(Vector3 from, Vector3 to);

// True when a standing capsule at `at` already overlaps something. CapsuleCast cannot report colliders it
// starts inside, so the destination is tested separately — without it, players mounted ladders through a
// barricade they were already overlapping.
public delegate bool CapsuleOccupied(Vector3 at);

// Unturned's ladder rules (PlayerStance.simulate's ladder block, PlayerMovement.simulate's CLIMB branch
// and InteractableLadder / PlayerStance.ReceiveClimbRequest), as pure functions.
//
// There are two ways onto a ladder and they are NOT the same code with the same rules — the game says so
// itself ("If revising ladder climbing rules please adjust InteractableLadder accordingly!"):
//
//   * walking into one. A 0.75 m probe runs every tick; it mounts, holds and drops the stance, and it does
//     not care how the ladder is angled.
//   * looking at one and interacting. Reaches 4 m, refuses angled ladders, demands line of sight to the
//     middle of the capsule it is about to create, and lands the player facing the ladder.
//
// Positions are this project's Godot world space. Every test here is a dot product or an axis-wise
// composition, both of which survive the Unity->Godot Z flip unchanged, so the maths is the game's own.
public static class PlayerLadder
{
    // PlayerStance.canCurrentStanceTransitionToClimbing. (SWIM is in the game's list too; this port has no
    // swimming stance yet, so a swimmer cannot be refused here and no branch is missing.)
    public static bool CanTransitionToClimbing(EPlayerStance stance) =>
        stance is EPlayerStance.Stand or EPlayerStance.Sprint;

    // Where the probe starts: half a metre above the feet, and it travels straight forward from there.
    public static Vector3 ProbeOrigin(Vector3 feet) => feet + (Vector3.Up * PlayerConfig.LadderProbeHeight);

    // Positive when the probe hit the ladder's front face, negative when it hit the back — the ladder's up
    // axis IS its facing direction. Near zero means an edge or a rung, which is not a face to climb.
    public static float FaceAlignment(in LadderContact contact) => contact.Normal.Dot(contact.Up);

    public static bool FacesLadder(in LadderContact contact) =>
        Mathf.Abs(FaceAlignment(contact)) > PlayerConfig.LadderFaceAlignment;

    // "Prevent climbing angled ladders. Only mostly up/down ladders." The ladder's up axis is horizontal
    // on an upright ladder, so it is world up it must NOT align with.
    public static bool IsUpright(in LadderContact contact) =>
        Mathf.Abs(Vector3.Up.Dot(contact.Up)) <= PlayerConfig.LadderMaxTiltAlignment;

    // The touch path's mount: on the ladder's own axis, at the height the probe hit minus the half metre
    // it was fired from (so the feet land level with the hit), half a metre back out along the face.
    public static Vector3 MountPoint(in LadderContact contact) =>
        new Vector3(contact.Origin.X, contact.Point.Y - PlayerConfig.LadderProbeHeight, contact.Origin.Z)
        + (contact.Normal * PlayerConfig.LadderMountOffset);

    // One tick of PlayerStance.simulate's ladder block. `contact` is whatever the caller's 0.75 m forward
    // probe hit, or null when it met no ladder at all (including a hit on something that is not one: the
    // FIRST thing the probe meets is what decides, so a wall in the way is a miss).
    //
    // The two capsule tests are the caller's, because they are physics; their ORDER and the fact that a
    // blocked mount leaves the player walking rather than stuck are the rules, and they are here.
    public static ClimbDecision Resolve(EPlayerStance stance, Vector3 position, in LadderContact? contact,
        CapsuleSweepBlocked isPathBlocked, CapsuleOccupied isOccupied)
    {
        bool climbing = stance == EPlayerStance.Climb;
        if (!climbing && !CanTransitionToClimbing(stance))
            return new ClimbDecision(EClimbTransition.None);

        if (contact is not { } ladder || !FacesLadder(ladder))
        {
            // Losing the ladder is how a climb ends: off the top, off the bottom, or off the side.
            return new ClimbDecision(climbing ? EClimbTransition.Dismount : EClimbTransition.None);
        }

        if (climbing)
            return new ClimbDecision(EClimbTransition.Hold);

        Vector3 mount = MountPoint(ladder);
        if (isPathBlocked(position, mount) || isOccupied(mount))
        {
            // Something solid stands between the player and the ladder's face, or where they would end up.
            // The game simply does not mount; the player keeps walking into whatever that is.
            return new ClimbDecision(EClimbTransition.None);
        }

        return new ClimbDecision(EClimbTransition.Mount, mount);
    }

    // --- The interact path: InteractableLadder.CanClimb / PlayerStance.ReceiveClimbRequest ---

    // The interact mount is a TELEPORT, and a teleport lands the player half a metre up, so the point it
    // aims at is that much lower — plus the probe's own half metre, plus a little extra downward so the
    // 0.75 m probe can reach back to the ladder on the very next tick and hold the climb.
    public const float InteractExtraDrop = 0.1f;

    // What has to fit at the mount: a standing capsule, the teleport's lift, and the same slack.
    public const float InteractTestHeight =
        PlayerConfig.HeightStand + InteractExtraDrop + PlayerConfig.TeleportVerticalOffset;

    public static Vector3 InteractMountPoint(in LadderContact contact) =>
        new Vector3(
            contact.Origin.X,
            contact.Point.Y - PlayerConfig.TeleportVerticalOffset - PlayerConfig.LadderProbeHeight
                - InteractExtraDrop,
            contact.Origin.Z)
        + (contact.Normal * PlayerConfig.LadderInteractTeleportOffset);

    // The middle of the capsule the mount would create. The game line-traces to it from the hit, so an
    // angled ladder cannot be used to place that capsule on the far side of a thin wall.
    public static Vector3 InteractLineOfSightTarget(Vector3 mountPoint) =>
        mountPoint + (Vector3.Up * (InteractTestHeight * 0.5f));

    // Facing for the interact mount: into the ladder. Unturned takes the ladder transform's own yaw and
    // adds 180 degrees when the back face was hit; the ladder's up axis is that facing direction, so this
    // is the same angle read off the same axis.
    //
    // Godot yaw: a body rotated by theta about Y looks along (-sin theta, 0, -cos theta).
    public static float ClimbYawDegrees(in LadderContact contact)
    {
        Vector3 into = FaceAlignment(contact) < 0f ? contact.Up : -contact.Up;
        return Mathf.RadToDeg(Mathf.Atan2(-into.X, -into.Z));
    }

    // Every rule InteractableLadder.CanClimb and ReceiveClimbRequest apply, in their order. The two world
    // queries are the caller's; both are asked only once the cheap geometric tests have passed, exactly as
    // the game asks them. `hasLineOfSight` is a line trace between the two points it is given — from the
    // hit, NOT from the eye: what it rules out is an angled ladder used to place the capsule on the far
    // side of a thin wall.
    public static bool CanInteractClimb(EPlayerStance stance, in LadderContact? contact,
        System.Func<Vector3, Vector3, bool> hasLineOfSight, System.Func<Vector3, bool> hasClearance,
        out Vector3 mountPoint, out float yawDegrees)
    {
        mountPoint = default;
        yawDegrees = 0f;

        if (stance == EPlayerStance.Climb || !CanTransitionToClimbing(stance))
            return false;
        if (contact is not { } ladder || !FacesLadder(ladder) || !IsUpright(ladder))
            return false;

        mountPoint = InteractMountPoint(ladder);
        yawDegrees = ClimbYawDegrees(ladder);
        return hasLineOfSight(ladder.Point, InteractLineOfSightTarget(mountPoint))
            && hasClearance(mountPoint);
    }

    // Where a validated interact mount actually puts the player: the teleport adds its vertical offset.
    public static Vector3 TeleportDestination(Vector3 mountPoint) =>
        mountPoint + (Vector3.Up * PlayerConfig.TeleportVerticalOffset);

    // --- PlayerMovement.simulate's CLIMB branch ---

    // Climbing is purely vertical, at half the climb speed, and only the forward/back axis drives it.
    // `moveZ` is Unturned's own input convention: +1 forward, -1 back.
    public static Vector3 ClimbVelocity(float moveZ, float speed) => new(0f, moveZ * speed * 0.5f, 0f);

    // `_isMoving` while climbing: either axis held, even though strafing does nothing on a ladder — the
    // stealth radius and the climbing animation both read it.
    public static bool IsMoving(float moveX, float moveZ) =>
        Mathf.Abs(moveX) > PlayerConfig.MoveInputDeadzone
        || Mathf.Abs(moveZ) > PlayerConfig.MoveInputDeadzone;

    // Holding forward while climbing queues half a metre per second of forward velocity for the tick the
    // player leaves the ladder, so stepping off the top reaches the surface in front even where air
    // acceleration is too low to build that speed from nothing. Anything else queues nothing.
    public static Vector3 LaunchVelocity(Basis facing, float moveZ) =>
        moveZ > PlayerConfig.MoveInputDeadzone
            ? facing * new Vector3(0f, 0f, -PlayerConfig.ClimbLaunchBoost) // Godot forward is -Z
            : Vector3.Zero;
}
