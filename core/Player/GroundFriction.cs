using System;
using Godot;
using UnturnedGodot.Assets;

namespace UnturnedGodot.Player;

// PlayerMovement.simulate's grounded velocity step (PlayerMovement.cs:1226-1270), both branches, as a
// pure function of the current velocity, the desired walk velocity, the floor normal and the surface's
// resolved friction.
//
// The port only ever had the first branch. Every surface behaved as ImmediatelyResponsive because
// nothing read Character_Friction_Mode off the physics material, so ice walked exactly like concrete —
// where the game's Ice.asset asks for half the deceleration (you slide) and 1.2x the max speed (you go
// faster). The surface under the player was already being resolved for footstep audio
// (PhysicsTool.GetTerrainMaterialName is ported), so the only thing missing was this arithmetic.
public static class GroundFriction
{
    // `velocity` is the current world velocity, `desiredWalkVelocity` is `transform.rotation *
    // move.normalized * speed` as the caller already computes it, `groundNormal` is the floor's, and
    // `speed` is the stance speed. Returns the new velocity.
    public static Vector3 Apply(Vector3 velocity, Vector3 desiredWalkVelocity, Vector3 groundNormal,
        float speed, CharacterFrictionProperties friction, float deltaTime)
    {
        // Both branches start from the same pair of cross products: the walk direction re-expressed in
        // the floor's plane, so walking down a ramp follows the ramp instead of stepping off it.
        Vector3 right = Vector3.Up.Cross(desiredWalkVelocity).Normalized();
        Vector3 alongFloor = right.Cross(groundNormal).Normalized();

        if (friction.Mode == EPhysicsMaterialCharacterFrictionMode.ImmediatelyResponsive)
        {
            // "Rather than adding gravity while grounded to smoothly walk down slopes, we adjust the
            // downward velocity to align with the floor plane. We do not allow an upward velocity here
            // because it would bounce us over the top of the ramp while walking up a slope."
            Vector3 result = alongFloor * speed;
            result.Y = MathF.Min(result.Y, 0f);
            return result;
        }

        Vector3 currentAlongFloor = ProjectOnPlane(velocity, groundNormal);
        float currentSpeedAlongFloor = currentAlongFloor.Length();

        Vector3 desiredAlongFloor = alongFloor * speed;
        // "note we do not clamp Y component here so that we can slide off jumps"
        desiredAlongFloor *= friction.MaxSpeedMultiplier;
        float desiredSpeed = desiredAlongFloor.Length();

        float maxSpeed;
        if (currentSpeedAlongFloor > desiredSpeed)
        {
            // "Base deceleration is 2.0 m/s²" — negative, and the Max floors it at the desired speed so
            // a decelerating body never overshoots past what it was aiming for.
            float deceleration = -PlayerConfig.GroundBaseDeceleration * friction.DecelerationMultiplier;
            maxSpeed = MathF.Max(desiredSpeed, currentSpeedAlongFloor + (deceleration * deltaTime));
        }
        else
        {
            maxSpeed = desiredSpeed;
        }

        // "Questionable units-wise, but pretend base acceleration is proportional to desired speed."
        // Note this is the multiplied desiredAlongFloor, so the max-speed multiplier scales the
        // acceleration as well — the original reuses the same vector and so does this.
        Vector3 acceleration = desiredAlongFloor * friction.AccelerationMultiplier;

        return ClampMagnitude(currentAlongFloor + (acceleration * deltaTime), maxSpeed);
    }

    // Whether a surface needs the ramp at all, so a caller can keep its existing instant path untouched
    // for the overwhelming majority of surfaces that do not.
    public static bool IsInstant(CharacterFrictionProperties friction) =>
        friction.Mode == EPhysicsMaterialCharacterFrictionMode.ImmediatelyResponsive;

    // Vector3.ProjectOnPlane. Godot has Vector3.Slide, which is the same operation, but it asserts the
    // normal is unit-length in debug builds and a floor normal off a raycast is only approximately so.
    internal static Vector3 ProjectOnPlane(Vector3 vector, Vector3 normal)
    {
        float sqr = normal.LengthSquared();
        if (sqr < 1e-12f)
            return vector;
        return vector - (normal * (vector.Dot(normal) / sqr));
    }

    // Unity's Vector3.ClampMagnitude: shortens to `max` only when longer, and a non-positive max
    // collapses the vector rather than flipping it.
    internal static Vector3 ClampMagnitude(Vector3 vector, float max)
    {
        if (max <= 0f)
            return Vector3.Zero;
        float sqr = vector.LengthSquared();
        if (sqr <= max * max)
            return vector;
        return vector * (max / MathF.Sqrt(sqr));
    }
}
