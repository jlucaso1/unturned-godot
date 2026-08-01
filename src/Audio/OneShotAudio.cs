using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// The positional one-shot service — our OneShotAudioParameters.Play(): any system (movement audio,
// future gunshots, melee impacts, explosions) asks for a definition to be played AT a world position
// with a volume scale and rolloff distance; the definition's own volumeMultiplier and pitch range
// apply on top, exactly as Unturned layers them. Discrete gameplay sounds are a CONSEQUENCE of
// replicated events rendered locally by each client — there is deliberately no "play sound" network
// message; a feature replicates its event and its client handler calls this.
//
// A fixed pool of AudioStreamPlayer3D voices is reused round-robin with oldest-voice stealing, so
// a firefight can't allocate unbounded audio nodes.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class OneShotAudio : Node3D
{
    private const int MaxVoices = 24;

    private readonly List<AudioStreamPlayer3D> _voices = new();
    private readonly Random _random = new();
    private AudioDefLibrary _library = null!;
    private int _nextVoice;

    public static OneShotAudio Create(AudioDefLibrary library) => new()
    {
        Name = "OneShotAudio",
        _library = library,
    };

    // Plays a random clip of the definition at a world position. volumeScale is the caller's gameplay
    // volume (e.g. FootstepConfig's 0.125), multiplied by the def's own volumeMultiplier; pitch is
    // randomized in the def's range unless the caller overrides it (zombie roars pitch by speciality:
    // megas growl at 0.5-0.7). Returns false when the definition isn't extracted yet.
    public bool Play(string defName, Vector3 position, float volumeScale, float maxDistance,
        float? minPitch = null, float? maxPitch = null)
    {
        AudioDefLibrary.Entry? entry = _library.Resolve(defName);
        if (entry == null)
            return false;

        AudioStreamPlayer3D voice = NextVoice();
        voice.Stream = entry.Clips[_random.Next(entry.Clips.Length)];
        voice.GlobalPosition = position;
        voice.MaxDistance = maxDistance;
        voice.VolumeDb = Mathf.LinearToDb(volumeScale * entry.Def.VolumeMultiplier);
        voice.PitchScale = Mathf.Lerp(minPitch ?? entry.Def.MinPitch, maxPitch ?? entry.Def.MaxPitch,
            (float)_random.NextDouble());
        voice.Play();
        if (OS.GetEnvironment("AUDIO_DEBUG") == "1")
        {
            Camera3D? cam = GetViewport().GetCamera3D();
            float dist = cam != null ? cam.GlobalPosition.DistanceTo(position) : -1f;
            Log.Print($"[audio] play '{defName}' at {position} listenerDist={dist:F1} " +
                $"maxDist={maxDistance} vol={voice.VolumeDb:F1}dB playing={voice.Playing} inTree={voice.IsInsideTree()}");
        }
        return true;
    }

    // Round-robin with oldest-voice stealing: prefer an idle voice; grow the pool up to the cap;
    // past the cap, retarget the next slot in ring order (the oldest started).
    private AudioStreamPlayer3D NextVoice()
    {
        for (int i = 0; i < _voices.Count; i++)
        {
            AudioStreamPlayer3D candidate = _voices[(_nextVoice + i) % _voices.Count];
            if (!candidate.Playing)
            {
                _nextVoice = (_voices.IndexOf(candidate) + 1) % Math.Max(1, _voices.Count);
                return candidate;
            }
        }

        if (_voices.Count < MaxVoices)
        {
            var voice = new AudioStreamPlayer3D
            {
                // Godot has no linear rolloff (Unturned's SetLinearRolloff); inverse-distance with this
                // unit size tracks it closely through the mid range, and MaxDistance still silences the
                // tail. MaxDb 0 removes Godot's near-field boost (Unity linear caps at unity gain), and
                // the distance lowpass is disabled — Unity doesn't muffle with distance, only attenuates.
                UnitSize = 6f,
                MaxDb = 0f,
                AttenuationFilterCutoffHz = 20500,
            };
            _voices.Add(voice);
            AddChild(voice);
            return voice;
        }

        AudioStreamPlayer3D stolen = _voices[_nextVoice];
        _nextVoice = (_nextVoice + 1) % _voices.Count;
        return stolen;
    }
}
