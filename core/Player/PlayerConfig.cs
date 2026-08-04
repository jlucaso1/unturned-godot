using System;

namespace UnturnedGodot.Player;

// Unturned's on-foot stances (SDG.Unturned.EPlayerStance, on-foot subset). Order kept for parity — the
// numbering is the game's own, and it goes over the wire as a byte.
public enum EPlayerStance
{
    Climb = 0,
    Sprint = 2,
    Stand = 3,
    Crouch = 4,
    Prone = 5,
}

// The exact numeric constants from Unturned's PlayerMovement / PlayerStance / PlayerLook (SI units: metres,
// m/s, seconds, degrees). These are hardcoded in the game source, so they are hardcoded here too, with the
// cited origin. Player-configurable / settings values live in PlayerSettings instead.
public static class PlayerConfig
{
    // Speeds (PlayerMovement.SPEED_*), m/s.
    public const float SpeedSprint = 7f;
    public const float SpeedStand = 4.5f;
    public const float SpeedCrouch = 2.5f;
    public const float SpeedProne = 1.5f;
    // SPEED_CLIMB. The climb branch then halves it (velocity.y = move.z * speed * 0.5), so a ladder is
    // actually ascended at 2.25 m/s; the full value is what the footstep cadence is derived from.
    public const float SpeedClimb = 4.5f;

    // Capsule heights per stance (PlayerMovement.HEIGHT_*), m. Radius is shared across stances.
    public const float HeightStand = 2f;
    public const float HeightCrouch = 1.2f;
    public const float HeightProne = 0.8f;
    public const float Radius = 0.4f;              // PlayerStance.RADIUS

    // Gravity is Unity's default (-9.81) times 3 (PlayerMovement.cs: "* deltaTime * 3"), so ~-29.43 m/s².
    public const float Gravity = -9.81f * 3f;
    public const float TerminalVelocity = -100f;   // vertical velocity clamp
    public const float JumpSpeed = 7f;             // PlayerMovement.JUMP, m/s

    // Airborne horizontal strafing (base accel 8, base decel 2, m/s²); ground movement is instant (no ramp).
    public const float AirAcceleration = 8f;
    public const float AirDeceleration = 2f;

    // First-person eye height per stance (PlayerLook.HEIGHT_LOOK_*), m. Lerped toward at EyeLerpRate.
    public const float EyeHeightStand = 1.75f;
    public const float EyeHeightCrouch = 1.2f;
    public const float EyeHeightProne = 0.35f;
    public const float EyeLerpRate = 4f;           // PlayerLook camera/eye lerps use rate 4
    public const float FovLerpRate = 8f;           // PlayerLook FOV lerps use rate 8

    // Third-person camera (PlayerLook: origin at the stance eye height, ~2 m back over the shoulder).
    public const float ThirdPersonDistance = 2f;
    public const float ThirdPersonShoulder = 0.5f; // right offset (animator.shoulder * 1.0, ~0.5 at rest)
    public const float ThirdPersonUp = 0.25f;
    public const float CameraSweepRadius = 0.39f;  // NEAR_CLIP_SWEEP_RADIUS, pulls the camera in on collision

    public const float MaxWalkableSlopeDegrees = 59f; // default (LevelInfo Max_Walkable_Slope -1 -> 59)

    // --- Ladders (PlayerStance.simulate's ladder block, InteractableLadder, Player.teleportToLocation) ---

    // The probe that mounts and holds a ladder: a ray from half a metre above the feet, straight forward,
    // 0.75 m long. Everything else about climbing follows from what it hits.
    public const float LadderProbeHeight = 0.5f;
    public const float LadderProbeRange = 0.75f;

    // Where the probe puts the player: on the ladder's own axis at the height it hit minus the probe's own
    // half metre, then half a metre back out along the surface normal.
    public const float LadderMountOffset = 0.5f;

    // |dot(hit normal, ladder up)| must exceed this for the hit to be the ladder's front or back face
    // rather than its edge. The ladder's "up" is its facing normal: its length runs along its local Z.
    public const float LadderFaceAlignment = 0.9f;

    // |dot(world up, ladder up)| must stay within this for the interact path to mount it — an angled
    // ladder is refused, only "mostly" up/down ones are climbable. The touch path does not test this.
    public const float LadderMaxTiltAlignment = 0.1f;

    // The interact path (PlayerStance.LADDER_INTERACT_*): a shorter reach than the general interaction ray
    // so its teleport is less powerful, and a step-out slightly under the 0.75 m the probe above will
    // then have to reach back with.
    public const float LadderInteractRange = 4f;
    public const float LadderInteractTeleportOffset = 0.65f;

    // Player.TELEPORT_VERTICAL_OFFSET and the padding teleportToLocation demands on top of the capsule.
    public const float TeleportVerticalOffset = 0.5f;
    public const float TeleportPadding = 0.5f;

    // Stepping off the top of a ladder while holding forward queues this much forward velocity, so a low
    // air-acceleration setting cannot leave the player unable to reach the surface in front of them.
    public const float ClimbLaunchBoost = 0.5f;

    // |input| over which an axis counts as held (PlayerMovement's `> 0.1` checks).
    public const float MoveInputDeadzone = 0.1f;

    // Unity's CharacterController silently lifts the capsule over obstacles up to m_StepOffset, and
    // Godot's CharacterBody3D has no equivalent — MoveAndSlide just stops at the ledge. Read straight
    // out of the masterbundle: every CharacterController in the game data carries m_StepOffset 0.5,
    // m_SkinWidth 0.1, m_SlopeLimit 75 (and PlayerMovement.SKIN_WIDTH is the same 0.1). Without this the
    // player is stopped by kerbs, stair treads and window sills the game walks straight over — a 0.83 m
    // jump (JUMP 7 against gravity 29.43) clears a sill only WITH the step-up on top of it.
    public const float StepOffset = 0.5f;
    public const float SkinWidth = 0.1f;             // CharacterController m_SkinWidth

    public static float SpeedFor(EPlayerStance stance) => stance switch
    {
        EPlayerStance.Climb => SpeedClimb,
        EPlayerStance.Sprint => SpeedSprint,
        EPlayerStance.Crouch => SpeedCrouch,
        EPlayerStance.Prone => SpeedProne,
        _ => SpeedStand,
    };

    // PlayerStance.getHeightForStance: only crouch and prone are shorter, so a climber keeps the standing
    // capsule — which is what the ladder's own clearance checks are sized against.
    public static float HeightFor(EPlayerStance stance) => stance switch
    {
        EPlayerStance.Crouch => HeightCrouch,
        EPlayerStance.Prone => HeightProne,
        _ => HeightStand, // Stand, Sprint and Climb share the standing height
    };

    public static float EyeHeightFor(EPlayerStance stance) => stance switch
    {
        EPlayerStance.Crouch => EyeHeightCrouch,
        EPlayerStance.Prone => EyeHeightProne,
        _ => EyeHeightStand, // PlayerLook: CLIMB looks from the standing height too
    };

    // Pitch clamp per stance, in Unturned's own convention converted to Godot pitch degrees where +up/-down
    // and 0 = horizon. Unturned uses 0=up/90=forward/180=down (PlayerLook MIN/MAX_ANGLE_*); here we store the
    // equivalent Godot pitch range [downLimit, upLimit].
    public static (float down, float up) PitchLimitsFor(EPlayerStance stance) => stance switch
    {
        // STAND/SPRINT: full 0..180 -> [-90, 90].
        EPlayerStance.Crouch => (-70f, 70f), // 20..160 -> down 70, up 70
        EPlayerStance.Prone => (-30f, 30f),  // 60..120
        // CLIMB: 45..100 -> up 45, down 10. Looking straight down a ladder is deliberately not allowed.
        EPlayerStance.Climb => (-10f, 45f),
        _ => (-89f, 89f),
    };

    // PlayerLook.updateLook clamps the pitch against the CURRENT stance every frame, so a stance change
    // re-clamps a view that has just become illegal. Doing it only where the look changes leaves a player
    // who mounts a ladder while staring straight down holding that view for as long as they keep the
    // mouse still — and the same for anyone who crouches or goes prone looking too far up or down.
    public static float ClampPitch(float pitchDegrees, EPlayerStance stance)
    {
        (float down, float up) = PitchLimitsFor(stance);
        return Math.Clamp(pitchDegrees, down, up);
    }
}
