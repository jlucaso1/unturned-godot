using System;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;
using UnturnedGodot.Player;

namespace UnturnedGodot;

// Movement sounds for ONE character — local or remote — derived purely from state (stance, moving,
// grounded, position), never from the network: each client computes every player's footsteps and
// landings from the replicated simulation state, exactly as Unturned's PlayerMovement does for
// !IsLocalPlayer channels. The ground material resolves through the terrain splat like
// PhysicsTool.GetTerrainMaterialName, and the one-shots go through the shared positional service.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed class MovementAudio
{
    private readonly PhysicsMaterialBank _bank;
    private readonly LandscapePhysics _landscape;
    private readonly SplatSampler _splat;
    private readonly OneShotAudio _audio;
    private readonly MovementSoundClock _clock;

    public MovementAudio(PhysicsMaterialBank bank, LandscapePhysics landscape, SplatSampler splat,
        OneShotAudio audio, bool startGrounded)
    {
        _bank = bank;
        _landscape = landscape;
        _splat = splat;
        _audio = audio;
        _clock = new MovementSoundClock(startGrounded);
    }

    public void Tick(EPlayerStance stance, bool moving, bool grounded, Vector3 position, float dt)
    {
        switch (_clock.Tick(stance, moving, grounded, PlayerConfig.SpeedFor(stance), dt))
        {
            case EMovementSound.Footstep:
                Play(FootstepConfig.FootstepKey(stance), stance, landing: false, position);
                break;
            case EMovementSound.Landed:
                Play(FootstepConfig.LandKey, stance, landing: true, position);
                break;
        }
    }

    private void Play(string key, EPlayerStance stance, bool landing, Vector3 position)
    {
        if (!_splat.TryGetDominantMaterial(position.X, -position.Z, out Guid materialGuid))
            return;
        string? materialName = _landscape.PhysicsNameOf(materialGuid);
        if (materialName == null)
            return;
        string? defPath = _bank.FindAudioDefPath(materialName, key);
        if (defPath == null)
            return;

        _audio.Play(AudioExtractor.DefNameOf(defPath), position,
            FootstepConfig.VolumeFor(stance, landing),
            landing ? FootstepConfig.LandMaxDistance : FootstepConfig.FootstepMaxDistance);
    }
}
