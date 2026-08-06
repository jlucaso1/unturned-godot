using System;
using Godot;
using UnturnedGodot.Assets;

namespace UnturnedGodot;

// The sound a fist makes when it lands — DamageTool.PlayMeleeImpactAudio, resolved through the physics
// material of whatever was struck.
//
// Entirely data-driven, and it has to be: the surface a hit reports is a Unity PhysicMaterial name, and
// which clip that plays is decided by the PhysicsMaterialAsset that claims the name, walking its Fallback
// chain until something defines the event. Nothing here knows that concrete sounds like concrete.
//
// The event key is MeleeImpact with LegacyImpact behind it, in that order. The original tries them the
// same way round for a melee hit and the other way round for the legacy impact a punch spawns — but a
// punch reaches ReceiveSpawnLegacyImpact, whose audio is the legacy one, and the shipped materials
// overwhelmingly define both to the same asset. The pair is what matters; a surface that defines only one
// of them still sounds.
public sealed class ImpactAudio
{
    // PlayMeleeImpactAudio: 0.6 times the definition's own multiplier, which OneShotAudio applies.
    public const float Volume = 0.6f;

    // SetLinearRolloff(1.0f, 16.0f). Godot has no linear rolloff, and OneShotAudio already models the
    // original's curve with an inverse-distance one — this is the distance at which it goes silent.
    public const float MaxDistance = 16f;

    // Tried in order; the first that resolves is what plays.
    public static readonly string[] EventKeys = { "MeleeImpact", "LegacyImpact" };

    private readonly PhysicsMaterialBank _bank;
    private readonly OneShotAudio _audio;
    private readonly Func<string, string> _bundleTagOf;

    public ImpactAudio(PhysicsMaterialBank bank, OneShotAudio audio, Func<string, string> bundleTagOf)
    {
        _bank = bank ?? throw new ArgumentNullException(nameof(bank));
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _bundleTagOf = bundleTagOf ?? throw new ArgumentNullException(nameof(bundleTagOf));
    }

    // Plays what `surface` sounds like at `position`, or nothing at all. Silence is a real outcome rather
    // than a failure: a collider with no PhysicMaterial reports no surface, and a surface whose asset
    // defines neither event key has no impact sound in the game either.
    public bool Play(string surface, Vector3 position)
    {
        if (string.IsNullOrEmpty(surface))
            return false;

        foreach (string key in EventKeys)
        {
            // The asset that DEFINED the key decides which bundle the clip came from, which is not always
            // the material the surface names: a workshop surface commonly falls back to a core one.
            if (_bank.FindAudioDef(surface, key) is not { } def)
                continue;
            return _audio.Play(AudioExtractor.DefKey(_bundleTagOf(def.Owner.Directory), def.Path),
                position, Volume, MaxDistance);
        }

        return false;
    }
}
