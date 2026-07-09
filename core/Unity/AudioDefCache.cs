using System.Collections.Generic;
using System.IO;

namespace UnturnedGodot.Unity;

// One extracted OneShotAudioDefinition: its pitch/volume settings and the cached .ogg clip file names,
// written once by the audio extraction pass and read at runtime to pick a random footstep/landing clip.
public sealed class OneShotAudioDef
{
    public float VolumeMultiplier { get; }
    public float MinPitch { get; }
    public float MaxPitch { get; }
    public IReadOnlyList<string> ClipFiles { get; }

    public OneShotAudioDef(float volumeMultiplier, float minPitch, float maxPitch, IReadOnlyList<string> clipFiles)
    {
        VolumeMultiplier = volumeMultiplier;
        MinPitch = minPitch;
        MaxPitch = maxPitch;
        ClipFiles = clipFiles;
    }
}

public static class AudioDefCache
{
    private const uint Magic = 0x31444155; // "UAD1"

    public static void Write(Stream stream, OneShotAudioDef def)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(def.VolumeMultiplier);
        w.Write(def.MinPitch);
        w.Write(def.MaxPitch);
        w.Write(def.ClipFiles.Count);
        foreach (string file in def.ClipFiles)
            w.Write(file);
    }

    public static OneShotAudioDef Read(Stream stream)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != Magic)
            throw new InvalidDataException("Not an audio def cache stream");
        float volume = r.ReadSingle();
        float minPitch = r.ReadSingle();
        float maxPitch = r.ReadSingle();
        var files = new List<string>(r.ReadInt32());
        for (int i = 0; i < files.Capacity; i++)
            files.Add(r.ReadString());
        return new OneShotAudioDef(volume, minPitch, maxPitch, files);
    }
}
