using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;
using UnturnedGodot.Player;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// The player's movement audio, mirroring PlayerMovement's footstep block: while grounded and moving (and
// not prone), every 2.1/speed seconds play a random clip from the ground material's FootstepWalk (or
// FootstepRun when sprinting) definition; on landing play its BipedLand. Jumping itself is silent in
// Unturned — the jump "sound" is the landing. The ground material comes from the terrain splatmap exactly
// like PhysicsTool.GetTerrainMaterialName (dominant layer -> LandscapeMaterialAsset -> physics name).
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class PlayerFootsteps : Node3D
{
    private PhysicsMaterialBank _bank = null!;
    private LandscapePhysics _landscape = null!;
    private SplatSampler _splat = null!;
    private string _audioCacheDir = "";

    private readonly Dictionary<string, OneShotAudioDef?> _defs = new();          // def name -> manifest
    private readonly Dictionary<string, AudioStreamOggVorbis[]> _streams = new(); // def name -> loaded clips
    private readonly Random _random = new();
    private AudioStreamPlayer3D _stepPlayer = null!;
    private AudioStreamPlayer3D _landPlayer = null!;
    private float _sinceFootstep;

    public static PlayerFootsteps Create(PhysicsMaterialBank bank, LandscapePhysics landscape,
        SplatSampler splat, string audioCacheDir)
    {
        var node = new PlayerFootsteps
        {
            Name = "Footsteps",
            _bank = bank,
            _landscape = landscape,
            _splat = splat,
            _audioCacheDir = audioCacheDir,
        };
        node._stepPlayer = OneShotPlayer("StepPlayer", FootstepConfig.FootstepMaxDistance);
        node._landPlayer = OneShotPlayer("LandPlayer", FootstepConfig.LandMaxDistance);
        node.AddChild(node._stepPlayer);
        node.AddChild(node._landPlayer);
        return node;
    }

    private static AudioStreamPlayer3D OneShotPlayer(string name, float maxDistance) => new()
    {
        Name = name,
        MaxDistance = maxDistance, // Unturned's SetLinearRolloff outer radius
        UnitSize = 6f,             // gentle start of falloff, ~the 1 m inner radius of the linear rolloff
    };

    // Called from the player's physics tick. Mirrors the simulate() footstep block: the clock only fires
    // while grounded, and PRONE/idle frames re-arm it silently (lastFootstep keeps advancing in Unturned).
    public void TickMovement(EPlayerStance stance, bool moving, bool grounded, float speed,
        Vector3 globalPosition, float delta)
    {
        _sinceFootstep += delta;
        if (_sinceFootstep <= FootstepConfig.Interval(speed))
            return;
        _sinceFootstep = 0f;

        if (!grounded || !moving || FootstepConfig.IsSilentStance(stance))
            return;
        Play(FootstepConfig.FootstepKey(stance), stance, landing: false, globalPosition);
    }

    // Called on the airborne -> grounded transition (PlayerMovement's onLanded -> PlayLandAudioClip).
    public void OnLanded(EPlayerStance stance, Vector3 globalPosition)
    {
        if (FootstepConfig.IsSilentStance(stance))
            return;
        if (Play(FootstepConfig.LandKey, stance, landing: true, globalPosition))
            _sinceFootstep = 0f; // PlayLandAudioClip resets lastFootstep
    }

    private bool Play(string key, EPlayerStance stance, bool landing, Vector3 globalPosition)
    {
        string? materialName = GroundMaterialName(globalPosition);
        if (materialName == null)
            return false;
        string? defPath = _bank.FindAudioDefPath(materialName, key);
        if (defPath == null)
            return false;

        string defName = AudioExtractor.DefNameOf(defPath);
        OneShotAudioDef? def = LoadDef(defName);
        if (def == null)
            return false;
        AudioStreamOggVorbis[] clips = LoadClips(defName, def);
        if (clips.Length == 0)
            return false;

        AudioStreamOggVorbis clip = clips[_random.Next(clips.Length)];
        float volume = FootstepConfig.VolumeFor(stance, landing) * def.VolumeMultiplier;
        float pitch = Mathf.Lerp(def.MinPitch, def.MaxPitch, (float)_random.NextDouble());

        AudioStreamPlayer3D player = landing ? _landPlayer : _stepPlayer;
        player.Stream = clip;
        player.VolumeDb = Mathf.LinearToDb(volume);
        player.PitchScale = pitch;
        player.Play();
        return true;
    }

    // PhysicsTool.GetTerrainMaterialName over our splat sampler. Object surfaces (building floors) don't
    // carry their prefab's PhysicMaterial yet, so they fall through to the terrain layer beneath — the
    // collider physic-material port is the follow-up that closes that gap.
    private string? GroundMaterialName(Vector3 globalPosition)
    {
        if (!_splat.TryGetDominantMaterial(globalPosition.X, -globalPosition.Z, out Guid materialGuid))
            return null;
        return _landscape.PhysicsNameOf(materialGuid);
    }

    private OneShotAudioDef? LoadDef(string defName)
    {
        if (_defs.TryGetValue(defName, out OneShotAudioDef? cached))
            return cached;
        string path = Path.Combine(_audioCacheDir, defName, "def.bin");
        if (!File.Exists(path))
            return null; // not cached as a miss: the one-time extraction may still be running in background
        using FileStream s = File.OpenRead(path);
        OneShotAudioDef def = AudioDefCache.Read(s);
        _defs[defName] = def;
        return def;
    }

    private AudioStreamOggVorbis[] LoadClips(string defName, OneShotAudioDef def)
    {
        if (_streams.TryGetValue(defName, out AudioStreamOggVorbis[]? cached))
            return cached;
        var clips = new List<AudioStreamOggVorbis>(def.ClipFiles.Count);
        foreach (string file in def.ClipFiles)
        {
            string path = Path.Combine(_audioCacheDir, defName, file);
            if (!File.Exists(path))
                continue;
            AudioStreamOggVorbis? stream = AudioStreamOggVorbis.LoadFromBuffer(File.ReadAllBytes(path));
            if (stream != null)
                clips.Add(stream);
        }
        AudioStreamOggVorbis[] result = clips.ToArray();
        _streams[defName] = result;
        return result;
    }
}
