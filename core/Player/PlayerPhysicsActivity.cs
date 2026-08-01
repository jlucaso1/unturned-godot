namespace UnturnedGodot.Player;

// Pure scheduling rule for the Godot character body. Once streaming is finished this project's collision
// world is static, so a grounded body with no requested state change has nothing for MoveAndSlide to solve.
// Networking, camera, animation and audio still tick; this only decides whether collision integration is
// necessary. Keeping the rule in Core makes every happy/bad transition executable in the hermetic suite.
public static class PlayerPhysicsActivity
{
    public static bool NeedsIntegration(bool worldReady, bool grounded, bool moving, bool jumping,
        bool stanceChanged) => !worldReady || !grounded || moving || jumping || stanceChanged;
}
