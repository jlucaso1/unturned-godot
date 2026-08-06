using System;
using System.Collections.Generic;
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
// Which keys, and in which order, is MovementAudioPlan.ImpactEventKeys — the same array the extraction
// plans from. Two copies of that list would fail silently: a key planned but never played, or played but
// never extracted, is a surface with no impact sound and no error anywhere.
public sealed class ImpactAudio
{
    // PlayMeleeImpactAudio: 0.6 times the definition's own multiplier, which OneShotAudio applies.
    public const float Volume = 0.6f;

    // SetLinearRolloff(1.0f, 16.0f). Godot has no linear rolloff, and OneShotAudio already models the
    // original's curve with an inverse-distance one — this is the distance at which it goes silent.
    public const float MaxDistance = 16f;

    // Tried in order; the first that resolves is what plays. One list, shared with the extraction.
    public static IReadOnlyList<string> EventKeys => MovementAudioPlan.ImpactEventKeys;

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
