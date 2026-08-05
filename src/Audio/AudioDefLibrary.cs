using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// Shared cache of extracted OneShotAudioDefinitions: def metadata (volume/pitch) plus decoded ogg
// clips, loaded lazily from the audio cache and kept for the session. Every sound system — movement
// audio today, gunshots and impacts tomorrow — resolves its definitions here, so a clip is decoded
// once no matter how many emitters use it. Misses aren't cached: the one-time background extraction
// may still be filling the cache.
public sealed class AudioDefLibrary
{
    public sealed record Entry(OneShotAudioDef Def, AudioStreamOggVorbis[] Clips);

    private readonly string _audioCacheDir;
    private readonly Dictionary<string, Entry> _entries = new();

    public AudioDefLibrary(string audioCacheDir) => _audioCacheDir = audioCacheDir;

    public Entry? Resolve(string defName)
    {
        if (_entries.TryGetValue(defName, out Entry? cached))
            return cached;

        string defPath = Path.Combine(_audioCacheDir, defName, "def.bin");
        if (!File.Exists(defPath))
            return null; // extraction may still be running; retry on the next play attempt

        OneShotAudioDef def;
        try
        {
            using FileStream s = File.OpenRead(defPath);
            def = AudioDefCache.Read(s);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // A DAMAGED marker is a missing one, which is the rule every other cache reader here follows.
            //
            // It has to be, because of where this runs. def.bin is written last, as the completeness
            // signal, and the write is not atomic — so a machine that lost power mid-write leaves a short
            // file that IsCached accepts and this cannot parse. The call chain from here is
            // OneShotAudio.Play <- MovementAudio.Tick <- the player's physics frame, so the throw did not
            // cost a footstep: it ended the session, on a cache the game itself wrote.
            //
            // Nulled rather than remembered, so a later extraction that rewrites the file is picked up
            // on the next attempt instead of this answer being cached for the session.
            Log.PrintErr($"[audio] '{defName}' has an unreadable def.bin ({e.Message}); "
                + "treating it as unextracted");
            return null;
        }

        var clips = new List<AudioStreamOggVorbis>(def.ClipFiles.Count);
        foreach (string file in def.ClipFiles)
        {
            string clipPath = Path.Combine(_audioCacheDir, defName, file);
            if (!File.Exists(clipPath))
                continue;
            AudioStreamOggVorbis? stream = AudioStreamOggVorbis.LoadFromBuffer(File.ReadAllBytes(clipPath));
            if (stream != null)
                clips.Add(stream);
        }
        if (clips.Count == 0)
            return null;

        var entry = new Entry(def, clips.ToArray());
        _entries[defName] = entry;
        return entry;
    }
}
