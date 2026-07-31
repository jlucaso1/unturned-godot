using Godot;

namespace UnturnedGodot.Player;

// The pure velocity maths from Unturned's PlayerMovement.simulate, factored out of the Godot node so the
// feel can be locked by unit tests. Collision, slope alignment and floor snapping are left to Godot's
// CharacterBody3D; these functions only decide the velocity vector.
public static class PlayerMovement
{
    // Ground movement on Unturned's default "ImmediatelyResponsive" material: the horizontal velocity is set
    // instantly to the desired walk velocity, with no acceleration ramp. wishDir is the yaw-rotated,
    // normalized input direction on the XZ plane. Vertical is left to the caller (snapping/gravity).
    public static Vector3 GroundVelocity(Vector3 wishDir, float speed)
        => new(wishDir.X * speed, 0f, wishDir.Z * speed);

    // Airborne: integrate 3x gravity (clamped to terminal velocity) and strafe the horizontal velocity
    // toward the wish velocity — base acceleration 8 m/s², base deceleration 2 m/s² — matching the air branch
    // of PlayerMovement.simulate.
    public static Vector3 AirVelocity(Vector3 velocity, Vector3 wishDir, float speed, float dt)
    {
        velocity.Y = Mathf.Max(PlayerConfig.TerminalVelocity, velocity.Y + (PlayerConfig.Gravity * dt));

        var horizontal = new Vector2(velocity.X, velocity.Z);
        Vector2 wish = new Vector2(wishDir.X, wishDir.Z) * speed;
        float currentSpeed = horizontal.Length();
        float wishSpeed = wish.Length();
        float maxSpeed = currentSpeed > wishSpeed
            ? Mathf.Max(wishSpeed, currentSpeed - (PlayerConfig.AirDeceleration * dt))
            : wishSpeed;

        horizontal += wish * (PlayerConfig.AirAcceleration * dt); // accel = wishVelocity * 8
        if (horizontal.Length() > maxSpeed)
            horizontal = horizontal.Normalized() * maxSpeed;

        return new Vector3(horizontal.X, velocity.Y, horizontal.Y);
    }
}
