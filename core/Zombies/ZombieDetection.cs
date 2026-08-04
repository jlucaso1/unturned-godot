using Godot;
using UnturnedGodot.Player;

namespace UnturnedGodot.Zombies;

// How zombies notice players — PlayerStance.GetStealthDetectionRadius + AlertTool.check, exactly:
// every 0.1 s the server emits a stealth alert around each alive player whose radius depends on stance
// and movement; a zombie inside the radius aggros unless the player is sneaking behind it.
public static class ZombieDetection
{
    public const float DetectInterval = 0.1f; // PlayerStance's lastDetect cadence

    // PlayerStance constants.
    public const float DetectMove = 1.1f;
    public const float DetectSprint = 20f;
    public const float DetectStand = 12f;
    public const float DetectCrouch = 6f;
    public const float DetectProne = 3f;

    // AlertTool clamps: at least 1 m (so the radius can always overlap a zombie), at most 64 m.
    public const float MinRadius = 1f;
    public const float MaxRadius = 64f;

    // Base-skill player (no SneakyBeaky mastery), Detect_Radius_Multiplier 1 (default config).
    public static float RadiusFor(EPlayerStance stance, bool moving)
    {
        float move = moving ? DetectMove : 1f;
        float radius = stance switch
        {
            EPlayerStance.Sprint => DetectSprint * move,
            EPlayerStance.Stand => DetectStand * move,
            // A climber is as quiet as a crouching player: GetStealthDetectionRadius groups CLIMB with
            // CROUCH, which is also why the clamp below matters — 6 * 1.1 is well inside the bounds.
            EPlayerStance.Crouch or EPlayerStance.Climb => DetectCrouch * move,
            EPlayerStance.Prone => DetectProne * move,
            _ => 0f,
        };
        return Mathf.Clamp(radius, MinRadius, MaxRadius);
    }

    // AlertTool.check's sneak rule: sneaking (any stance but sprint) behind a zombie that faces away
    // stays undetected. offset points from the player to the zombie; forward is the zombie's facing.
    public static bool IsDetected(Vector3 zombieForward, Vector3 playerToZombie, float sqrRadius, bool sneak)
    {
        if (playerToZombie.LengthSquared() > sqrRadius)
            return false; // too far away anyway
        if (sneak && zombieForward.Dot(playerToZombie.Normalized()) > 0.5f)
            return false; // behind them and sneaking
        return true;
    }
}
