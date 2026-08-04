using System;
using Godot;
using UnturnedGodot.Player;

namespace UnturnedGodot.Damage;

// Where a punch starts and which way it goes: `new Ray(player.look.aim.position, player.look.aim.forward)`,
// rebuilt from the state the server actually holds for a player.
//
// The server does not have the client's camera node, only the replicated position, stance and view
// angles — so the aim is derived from those. That is deliberate rather than a compromise: reconstructing
// it here means the ray the server damages with is a function of state the server already validated (the
// trusted position goes through a speed budget, the angles are quantized), instead of a second thing the
// client would have to be believed about.
public static class PunchAim
{
    // The eye the ray leaves from. PlayerLook parents its aim to the head, which PlayerController puts
    // at the stance's eye height — so this is the same point the owner's own client-side ray starts at,
    // up to the eye-height lerp the camera smooths through on a stance change.
    public static Vector3 Origin(Vector3 position, EPlayerStance stance) =>
        position + (Vector3.Up * PlayerConfig.EyeHeightFor(stance));

    // The forward the ray travels. `yawDegrees` is the body yaw the input carried and `pitchDegrees` is
    // the Godot look pitch — 0 at the horizon, positive up — which is the head's rotation about X on top
    // of the body's about Y. Composing them in that order gives the camera's -Z.
    //
    // At a level pitch this reduces to (-sin yaw, 0, -cos yaw), the same facing convention the zombie
    // brain uses, which is what lets the backstab test compare the two directly.
    public static Vector3 Forward(float yawDegrees, float pitchDegrees)
    {
        float yaw = Mathf.DegToRad(yawDegrees);
        float pitch = Mathf.DegToRad(pitchDegrees);
        float cosPitch = MathF.Cos(pitch);
        return new Vector3(-cosPitch * MathF.Sin(yaw), MathF.Sin(pitch), -cosPitch * MathF.Cos(yaw));
    }

    // The wire carries pitch as 0..180 with 90 at the horizon (PlayerController sends `_pitch + 90`, and
    // NetAngles quantizes whole degrees), so anything reading a replicated pitch has to subtract the
    // offset before it is an angle again. One place, because getting it wrong points every punch at the
    // sky in a way nothing else in the state stream would notice.
    public const float WirePitchOffset = 90f;

    public static float PitchFromWire(float wirePitchDegrees) => wirePitchDegrees - WirePitchOffset;
}
