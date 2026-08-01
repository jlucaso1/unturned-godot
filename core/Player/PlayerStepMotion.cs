using Godot;

namespace UnturnedGodot.Player;

// Pure part of the CharacterController-style step pass. Keeping the displacement decision outside the
// physics server makes its invariants unit-testable: a successful step may complete the requested motion,
// but must never replay distance MoveAndSlide already delivered.
public static class PlayerStepMotion
{
    public const float DeliveredEnough = 0.8f;

    public static bool TryRemaining(bool isOnWall, Vector3 beforeMove, Vector3 afterMove,
        Vector3 wantedHorizontal, out Vector3 remaining)
    {
        remaining = Vector3.Zero;
        if (!isOnWall || wantedHorizontal.LengthSquared() < 1e-8f)
            return false;

        Vector3 moved = afterMove - beforeMove;
        moved.Y = 0f;
        if (moved.LengthSquared()
            >= wantedHorizontal.LengthSquared() * (DeliveredEnough * DeliveredEnough))
            return false;

        // Vector subtraction deliberately removes sideways slide too: afterMove + remaining lands on
        // beforeMove + wantedHorizontal in XZ, rather than compounding the slide or requested distance.
        remaining = wantedHorizontal - moved;
        remaining.Y = 0f;
        return true;
    }
}
