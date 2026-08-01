using Godot;
using UnturnedGodot.Player;

namespace UnturnedGodot;

// Unity's CharacterController lifts the capsule over obstacles up to m_StepOffset as part of its move;
// Godot's MoveAndSlide has no equivalent and simply stops at the ledge. This restores that step for any
// CharacterBody3D, so kerbs, stair treads and low sills behave the way the game's own controller does.
//
// It lives apart from PlayerController so the behaviour can be exercised against a real physics world
// without a player, a camera or input — see STEP_PROBE in Main.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class PlayerStep
{
    // Call right after MoveAndSlide, with where the body started and the horizontal motion it wanted.
    // Returns true when it climbed. `beforeMove` is the pre-move global position.
    public static bool TryStepUp(CharacterBody3D body, Vector3 beforeMove, Vector3 wantedHorizontal)
    {
        // Avoid even crossing into the physics server while idle/falling vertically. This is the common
        // case, and IsOnWall cannot affect the answer when there is no horizontal displacement to finish.
        if (wantedHorizontal.LengthSquared() < 1e-8f
            || !PlayerStepMotion.TryRemaining(body.IsOnWall(), beforeMove, body.GlobalPosition,
                wantedHorizontal, out Vector3 remaining))
            return false;

        Vector3 up = Vector3.Up * PlayerConfig.StepOffset;
        Transform3D start = body.GlobalTransform;

        // 1. Headroom to lift the capsule.
        if (body.TestMove(start, up))
            return false;
        Transform3D raised = start.Translated(up);

        // 2. Only what the slide did NOT deliver, retried from up there. `start` is the position AFTER
        //    MoveAndSlide, so it already carries `moved`; adding the full wanted motion on top would
        //    travel it twice — up to 1.8x the requested distance, since the gate above admits anything
        //    under 80% delivered. Unity's controller does the step inside the move and never overshoots
        //    the displacement it was asked for, so completing the shortfall is what matches it. Vector
        //    subtraction, not scalar: a slide that went sideways is corrected back onto the wanted target.
        if (body.TestMove(raised, remaining))
            return false;
        Transform3D advanced = raised.Translated(remaining);

        // 3. Land: there must be something solid within the step height, or this is a gap and stepping
        //    into it would float the body. Deliberately NOT filtered by surface angle — Unity's step pass
        //    does not test the tread's normal either, and doing so rejected real steps whose descent
        //    clips the sloped face of the riser on the way down. Climbing is already bounded by the step
        //    height and by requiring clear space horizontally once raised.
        var landing = new KinematicCollision3D();
        if (!body.TestMove(advanced, Vector3.Down * (PlayerConfig.StepOffset + PlayerConfig.SkinWidth), landing))
            return false;

        body.GlobalPosition = advanced.Origin + (Vector3.Down * landing.GetTravel().Length());
        return true;
    }
}
