using System.Collections.Generic;
using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The positional one-shot service and the definition cache behind it.
//
// Both are engine-bound for the same reason: a clip is an AudioStreamOggVorbis and a voice is an
// AudioStreamPlayer3D, neither of which exists outside Godot. The decoding and the pooling are where the
// interesting failures live — a definition that never resolves is silence, and a voice pool that grows
// without a cap is a firefight allocating audio nodes forever.
//
// Fixtures/tone.ogg is 2.7 KB of generated sine, not game content: the library only returns an entry when
// at least one clip actually decodes, so testing the path that works needs a file that really is Vorbis.
public class AudioTests : TestClass
{
    public AudioTests(Node testScene) : base(testScene) { }

    // A definition whose cache directory does not exist yet. Extraction runs in the background, so this
    // is the ordinary state early in a session — it must be a quiet miss rather than a throw, and it must
    // NOT be cached, or the sound would stay silent for the rest of the session once the cache filled.
    [Test]
    public void AMissingDefinitionResolvesToNothingAndIsNotCached()
    {
        using var dir = new TempDir();
        var library = new AudioDefLibrary(dir.Path);

        Assert.Null(library.Resolve("core-bipedland-grass"));

        // Now the extraction "finishes" and the same name resolves — which only works if the miss above
        // was not remembered.
        WriteDef(dir.Path, "core-bipedland-grass", volume: 0.5f, minPitch: 0.9f, maxPitch: 1.1f);
        Assert.NotNull(library.Resolve("core-bipedland-grass"));
    }

    // A definition whose clip files are all missing is not an entry: an entry with no clips would be
    // picked for playback and then index an empty array.
    [Test]
    public void ADefinitionWithNoDecodableClipsResolvesToNothing()
    {
        using var dir = new TempDir();
        string defDir = Path.Combine(dir.Path, "broken");
        Directory.CreateDirectory(defDir);
        using (FileStream s = File.Create(Path.Combine(defDir, "def.bin")))
            AudioDefCache.Write(s, new OneShotAudioDef(1f, 1f, 1f, new[] { "gone.ogg" }));

        Assert.Null(new AudioDefLibrary(dir.Path).Resolve("broken"));
    }

    // The decode happens once per definition, however many emitters ask for it — hundreds of footsteps a
    // minute resolve through here.
    [Test]
    public void AResolvedDefinitionIsCachedAndItsSettingsSurvive()
    {
        using var dir = new TempDir();
        WriteDef(dir.Path, "footstep", volume: 0.25f, minPitch: 0.8f, maxPitch: 1.2f);
        var library = new AudioDefLibrary(dir.Path);

        AudioDefLibrary.Entry? first = library.Resolve("footstep");
        AudioDefLibrary.Entry? second = library.Resolve("footstep");

        Assert.NotNull(first);
        Assert.Same(first, second); // the second call did not re-read or re-decode
        Assert.Equal(0.25f, first!.Def.VolumeMultiplier);
        Assert.Equal(0.8f, first.Def.MinPitch);
        Assert.Equal(1.2f, first.Def.MaxPitch);
        Assert.Single(first.Clips);
    }

    // Asking for a definition that is not extracted is a false rather than a throw — the caller is a
    // footstep on a surface whose audio has not landed yet, and it simply stays quiet.
    [Test]
    public void PlayingAnUnknownDefinitionIsRefusedQuietly()
    {
        using var dir = new TempDir();
        OneShotAudio audio = OneShotAudio.Create(new AudioDefLibrary(dir.Path));
        TestScene.AddChild(audio);

        Assert.False(audio.Play("nothing-here", Vector3.Zero, volumeScale: 1f, maxDistance: 32f));

        audio.QueueFree();
    }

    // The definition's own envelope is applied on top of the caller's gameplay volume, the way Unturned
    // layers them, and the voice is placed in the world rather than played flat.
    [Test]
    public void APlayedSoundTakesItsPositionAndTheDefinitionsVolume()
    {
        using var dir = new TempDir();
        WriteDef(dir.Path, "footstep", volume: 0.5f, minPitch: 1f, maxPitch: 1f);
        OneShotAudio audio = OneShotAudio.Create(new AudioDefLibrary(dir.Path));
        TestScene.AddChild(audio);

        var where = new Vector3(10f, 2f, -30f);
        Assert.True(audio.Play("footstep", where, volumeScale: 0.25f, maxDistance: 48f));

        AudioStreamPlayer3D voice = Voices(audio)[0];
        Assert.Equal(where, voice.GlobalPosition);
        Assert.Equal(48f, voice.MaxDistance);
        // 0.25 (caller) * 0.5 (definition) = 0.125 linear.
        Assert.True(Mathf.Abs(voice.VolumeDb - Mathf.LinearToDb(0.125f)) < 0.01f,
            $"volume was {voice.VolumeDb} dB, expected {Mathf.LinearToDb(0.125f)} dB");
        // Pitch pinned by the definition's own range.
        Assert.Equal(1f, voice.PitchScale);

        audio.QueueFree();
    }

    // A caller can override the definition's pitch range: megas growl at 0.5-0.7 where a normal zombie
    // roars at the definition's own envelope.
    [Test]
    public void ACallerCanOverrideTheDefinitionsPitchRange()
    {
        using var dir = new TempDir();
        WriteDef(dir.Path, "roar", volume: 1f, minPitch: 0.9f, maxPitch: 1.1f);
        OneShotAudio audio = OneShotAudio.Create(new AudioDefLibrary(dir.Path));
        TestScene.AddChild(audio);

        Assert.True(audio.Play("roar", Vector3.Zero, 1f, 64f, minPitch: 0.6f, maxPitch: 0.6f));

        Assert.Equal(0.6f, Voices(audio)[0].PitchScale);
        audio.QueueFree();
    }

    // The pool is bounded. Past its cap the oldest voice is retargeted rather than a new node allocated,
    // which is what stops a firefight from growing the scene tree without limit.
    [Test]
    public void TheVoicePoolIsBoundedAndStealsTheOldestVoice()
    {
        using var dir = new TempDir();
        WriteDef(dir.Path, "shot", volume: 1f, minPitch: 1f, maxPitch: 1f);
        OneShotAudio audio = OneShotAudio.Create(new AudioDefLibrary(dir.Path));
        TestScene.AddChild(audio);

        for (int i = 0; i < 200; i++)
            Assert.True(audio.Play("shot", new Vector3(i, 0f, 0f), 1f, 32f));

        List<AudioStreamPlayer3D> voices = Voices(audio);
        Assert.True(voices.Count <= 24, $"the pool grew to {voices.Count} voices");
        // Every play still landed somewhere: the last one retargeted a voice rather than being dropped.
        Assert.Contains(voices, v => v.GlobalPosition.X == 199f);

        audio.QueueFree();
    }

    // An idle voice is preferred over stealing a busy one, which is the whole point of walking the ring
    // before growing the pool: a stopped voice is free and a playing one is somebody's footstep.
    [Test]
    public void AnIdleVoiceIsReusedBeforeANewOneIsMade()
    {
        using var dir = new TempDir();
        WriteDef(dir.Path, "footstep", volume: 1f, minPitch: 1f, maxPitch: 1f);
        OneShotAudio audio = OneShotAudio.Create(new AudioDefLibrary(dir.Path));
        TestScene.AddChild(audio);

        audio.Play("footstep", Vector3.Zero, 1f, 32f);
        List<AudioStreamPlayer3D> after1 = Voices(audio);
        Assert.Single(after1);

        after1[0].Stop(); // that footstep finished
        audio.Play("footstep", Vector3.Right, 1f, 32f);

        Assert.Single(Voices(audio)); // reused, not grown
        Assert.Equal(Vector3.Right, after1[0].GlobalPosition);

        audio.QueueFree();
    }

    // AUDIO_DEBUG=1 prints where a sound was placed relative to the listener. It exists because "I cannot
    // hear it" and "it is not playing" look identical from the outside, and it runs inside Play — so if it
    // throws, it throws on a gameplay sound rather than in a diagnostic.
    [Test]
    public void TheDebugTraceRunsWithoutDisturbingPlayback()
    {
        using var dir = new TempDir();
        WriteDef(dir.Path, "footstep", volume: 1f, minPitch: 1f, maxPitch: 1f);
        OneShotAudio audio = OneShotAudio.Create(new AudioDefLibrary(dir.Path));
        TestScene.AddChild(audio);

        string previous = OS.GetEnvironment("AUDIO_DEBUG");
        OS.SetEnvironment("AUDIO_DEBUG", "1");
        try
        {
            Assert.True(audio.Play("footstep", new Vector3(1f, 2f, 3f), 1f, 32f));
            Assert.Equal(new Vector3(1f, 2f, 3f), Voices(audio)[0].GlobalPosition);
        }
        finally
        {
            OS.SetEnvironment("AUDIO_DEBUG", previous);
        }

        audio.QueueFree();
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static List<AudioStreamPlayer3D> Voices(OneShotAudio audio)
    {
        var found = new List<AudioStreamPlayer3D>();
        foreach (Node child in audio.GetChildren())
            if (child is AudioStreamPlayer3D voice)
                found.Add(voice);
        return found;
    }

    // Writes a definition directory the way the extraction pass does: def.bin plus its clip files.
    private static void WriteDef(string cacheDir, string name, float volume, float minPitch, float maxPitch)
    {
        string defDir = Path.Combine(cacheDir, name);
        Directory.CreateDirectory(defDir);
        File.Copy(ProjectSettings.GlobalizePath("res://tests/Runtime/Fixtures/tone.ogg"),
            Path.Combine(defDir, "clip0.ogg"), overwrite: true);
        using FileStream stream = File.Create(Path.Combine(defDir, "def.bin"));
        AudioDefCache.Write(stream, new OneShotAudioDef(volume, minPitch, maxPitch, new[] { "clip0.ogg" }));
    }

    private sealed class TempDir : System.IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "unturned-godot-audio-" + System.Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }
}
