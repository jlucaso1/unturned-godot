using System;

namespace UnturnedGodot.Player;

// The footstep/landing audio rules from PlayerMovement's simulate tick and Play*AudioClip methods.
public static class FootstepConfig
{
    public const float FootstepVolume = 0.125f;      // PlayFootstepAudioClip: volume *= 0.125f
    public const float LandVolume = 0.15f;           // PlayLandAudioClip: volume *= 0.15f
    public const float CrouchVolumeMultiplier = 0.5f; // both: crouch halves the volume
    public const float FootstepMaxDistance = 32f;    // SetLinearRolloff(1, 32)
    public const float LandMaxDistance = 24f;        // SetLinearRolloff(1, 24)

    // Footsteps fire when Time - lastFootstep > 2.1 / speed (PlayerMovement.simulate).
    public static float Interval(float speed) => speed > 0f ? 2.1f / speed : float.PositiveInfinity;

    // PlayFootstepAudioClip: sprint uses the run definitions, everything else walks.
    public static string FootstepKey(EPlayerStance stance) =>
        stance == EPlayerStance.Sprint ? "FootstepRun" : "FootstepWalk";

    public const string LandKey = "BipedLand";

    // No footsteps while prone, and prone landings are silent too (PlayLandAudioClip early-out).
    public static bool IsSilentStance(EPlayerStance stance) => stance == EPlayerStance.Prone;

    public static float VolumeFor(EPlayerStance stance, bool landing)
    {
        float volume = landing ? LandVolume : FootstepVolume;
        return stance == EPlayerStance.Crouch ? volume * CrouchVolumeMultiplier : volume;
    }
}
