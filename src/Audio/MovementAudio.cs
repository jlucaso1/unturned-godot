using System;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;
using UnturnedGodot.Player;
using UnturnedGodot.Unity;

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

    // Turns the directory a physics material was scanned from into the tag of the bundle that carries its
    // audio. Definitions are cached per bundle, because two bundles can name one the same thing.
    private readonly Func<string, string> _bundleTagOf;

    public MovementAudio(PhysicsMaterialBank bank, LandscapePhysics landscape, SplatSampler splat,
        OneShotAudio audio, bool startGrounded, Func<string, string> bundleTagOf)
    {
        _bank = bank;
        _landscape = landscape;
        _splat = splat;
        _audio = audio;
        _clock = new MovementSoundClock(startGrounded);
        _bundleTagOf = bundleTagOf;
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
        // A stance can name its own surface: checkGround hardcodes "Tile" for a climbing player, who is
        // hearing a ladder's rungs rather than whatever happens to be on the ground far below them.
        string? materialName = FootstepConfig.MaterialOverrideFor(stance);
        if (materialName == null)
        {
            if (!_splat.TryGetDominantMaterial(position.X, -position.Z, out Guid materialGuid))
                return;
            materialName = _landscape.PhysicsNameOf(materialGuid);
        }
        if (materialName == null)
            return;
        // The asset that DEFINED the key decides which bundle the audio came from, which is not always
        // the material the surface names: a workshop surface commonly falls back to a core one.
        if (_bank.FindAudioDef(materialName, key) is not { } def)
            return;

        _audio.Play(AudioExtractor.DefKey(_bundleTagOf(def.Owner.Directory), def.Path), position,
            FootstepConfig.VolumeFor(stance, landing),
            landing ? FootstepConfig.LandMaxDistance : FootstepConfig.FootstepMaxDistance);
    }
}
