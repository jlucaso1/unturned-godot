namespace UnturnedGodot.Player;

// Unturned's on-foot stances (SDG.Unturned.EPlayerStance, on-foot subset). Order kept for parity.
public enum EPlayerStance
{
    Sprint = 2,
    Stand = 3,
    Crouch = 4,
    Prone = 5,
}

// How a hold/toggle control behaves (Unturned's ControlsSettings modes).
public enum EControlMode
{
    Hold,
    Toggle,
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

    // Third-person camera (PlayerLook: origin at eye height, ~2 m back over the shoulder).
    public const float ThirdPersonEyeHeight = 1.75f;
    public const float ThirdPersonDistance = 2f;
    public const float ThirdPersonShoulder = 0.5f; // right offset (animator.shoulder * 1.0, ~0.5 at rest)
    public const float ThirdPersonUp = 0.25f;
    public const float CameraSweepRadius = 0.39f;  // NEAR_CLIP_SWEEP_RADIUS, pulls the camera in on collision

    public const float MaxWalkableSlopeDegrees = 59f; // default (LevelInfo Max_Walkable_Slope -1 -> 59)

    public static float SpeedFor(EPlayerStance stance) => stance switch
    {
        EPlayerStance.Sprint => SpeedSprint,
        EPlayerStance.Crouch => SpeedCrouch,
        EPlayerStance.Prone => SpeedProne,
        _ => SpeedStand,
    };

    public static float HeightFor(EPlayerStance stance) => stance switch
    {
        EPlayerStance.Crouch => HeightCrouch,
        EPlayerStance.Prone => HeightProne,
        _ => HeightStand, // Stand and Sprint share the standing height
    };

    public static float EyeHeightFor(EPlayerStance stance) => stance switch
    {
        EPlayerStance.Crouch => EyeHeightCrouch,
        EPlayerStance.Prone => EyeHeightProne,
        _ => EyeHeightStand,
    };

    // Pitch clamp per stance, in Unturned's own convention converted to Godot pitch degrees where +up/-down
    // and 0 = horizon. Unturned uses 0=up/90=forward/180=down (PlayerLook MIN/MAX_ANGLE_*); here we store the
    // equivalent Godot pitch range [downLimit, upLimit].
    public static (float down, float up) PitchLimitsFor(EPlayerStance stance) => stance switch
    {
        // STAND/SPRINT: full 0..180 -> [-90, 90].
        EPlayerStance.Crouch => (-70f, 70f), // 20..160 -> down 70, up 70
        EPlayerStance.Prone => (-30f, 30f),  // 60..120
        _ => (-89f, 89f),
    };
}
