using System.Collections.Generic;
using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Where a decoded texture meets the materials waiting for it.
//
// This is engine-bound at both ends — it creates GPU ImageTextures and assigns them onto Materials — and
// it is also where the cold load's hardest ordering problem lives. A material is built long before its
// texture exists: on a cold load the .tex is still somewhere in the .resS tail being decoded. So the
// registry has to answer "are these two materials the same?" before it can possibly know, remember that
// the answer was a guess, and settle it later.
//
// Get that wrong in the safe direction and materials never merge, costing draw calls. Get it wrong in the
// unsafe direction and two textures that only LOOK alike get merged, which is a visual bug nothing else
// would catch.
public class TextureRegistryTests : TestClass
{
    public TextureRegistryTests(Node testScene) : base(testScene) { }

    // Byte-identical texture data reached through two different cache keys is one identity. The whole
    // .tex is hashed — format, dimensions, mip count, filter mode and pixels — so this can never merge
    // two textures whose sampling or visual result differs.
    [Test]
    public void TwoKeysOverIdenticalTextureDataShareOneIdentity()
    {
        using var dir = new TempDir();
        WriteTexture(dir.Path, "core_1", 255, 0, 0);
        WriteTexture(dir.Path, "mod_2", 255, 0, 0);   // same bytes, different key
        WriteTexture(dir.Path, "mod_3", 0, 255, 0);   // different bytes
        var registry = new TextureRegistry(dir.Path);

        string first = registry.MaterialIdentity("core_1");
        string second = registry.MaterialIdentity("mod_2");
        string third = registry.MaterialIdentity("mod_3");

        Assert.Equal(first, second);
        Assert.NotEqual(first, third);
        Assert.Empty(registry.ProvisionalIdentityKeys); // all three were on disk: nothing was a guess
        Assert.True(registry.MaterialAliasCount > 0);
    }

    // The cold-load case: the .tex is not there yet, so the key stands in for its own identity and
    // nothing merges. Recorded as PROVISIONAL, because that answer stops being true the moment the
    // texture lands — and a registry that forgot which answers were guesses could never revisit them.
    [Test]
    public void AnIdentityGuessedBeforeItsTextureLandsIsRememberedAsProvisional()
    {
        using var dir = new TempDir();
        var registry = new TextureRegistry(dir.Path);

        Assert.Equal("core_1", registry.MaterialIdentity("core_1")); // stands in for itself
        Assert.Contains("core_1", registry.ProvisionalIdentityKeys);
    }

    // Settling: identities resolved off the main thread come back and replace the guesses. Only the
    // guesses — an identity a material was already built on must not change underneath it, or two
    // surfaces that were deliberately kept apart would silently merge mid-session.
    [Test]
    public void SettlingReplacesOnlyTheGuesses()
    {
        using var dir = new TempDir();
        WriteTexture(dir.Path, "settled", 1, 2, 3);
        var registry = new TextureRegistry(dir.Path);

        string alreadyKnown = registry.MaterialIdentity("settled"); // real, from disk
        registry.MaterialIdentity("guessed");                       // provisional

        int changed = registry.ResolvedIdentities(new Dictionary<string, string>
        {
            ["guessed"] = "the-real-identity",
            ["settled"] = "something-else",  // must be ignored: it was never a guess
            ["never-asked"] = "irrelevant",  // never provisional either
        });

        Assert.Equal(1, changed);
        Assert.Equal("the-real-identity", registry.MaterialIdentities["guessed"]);
        Assert.Equal(alreadyKnown, registry.MaterialIdentities["settled"]);
        Assert.Empty(registry.ProvisionalIdentityKeys);
    }

    // An empty identity is not an answer, so it must not settle a guess — that would replace a workable
    // stand-in with nothing.
    [Test]
    public void AnEmptyResolvedIdentityDoesNotSettleAnything()
    {
        using var dir = new TempDir();
        var registry = new TextureRegistry(dir.Path);
        registry.MaterialIdentity("guessed");

        Assert.Equal(0, registry.ResolvedIdentities(new Dictionary<string, string> { ["guessed"] = "" }));
        Assert.Contains("guessed", registry.ProvisionalIdentityKeys);
    }

    // Materials queue against the key they are waiting for, and applying the texture reaches all of them.
    // The pending entry is drained, because a key that has been applied is done with.
    [Test]
    public void ApplyingAKeyTexturesEveryMaterialWaitingOnIt()
    {
        using var dir = new TempDir();
        WriteTexture(dir.Path, "grass", 10, 200, 10);
        var registry = new TextureRegistry(dir.Path);

        var first = new StandardMaterial3D();
        var second = new StandardMaterial3D();
        registry.Register("grass", first);
        registry.Register("grass", second);
        registry.Register("grass", first); // the same material twice must not queue twice
        Assert.Equal(1, registry.PendingKeyCount);

        Assert.True(registry.Apply("grass"));

        Assert.NotNull(first.AlbedoTexture);
        Assert.NotNull(second.AlbedoTexture);
        // The same decoded image, not two uploads of identical pixels.
        Assert.Same(first.AlbedoTexture, second.AlbedoTexture);
        Assert.Equal(0, registry.PendingKeyCount);
    }

    // Applying a key nobody registered, or one whose .tex never arrived, is false rather than a throw:
    // the texture phase walks keys the map referenced, and not all of them resolve.
    [Test]
    public void ApplyingAnUnknownOrUndecodableKeyIsRefused()
    {
        using var dir = new TempDir();
        var registry = new TextureRegistry(dir.Path);

        Assert.False(registry.Apply("nobody-registered-this"));

        registry.Register("missing-on-disk", new StandardMaterial3D());
        Assert.False(registry.Apply("missing-on-disk"));
    }

    // An empty key is not a texture reference — a material with no _MainTex — and must not create a
    // pending entry that nothing will ever drain.
    [Test]
    public void AnEmptyKeyIsNotRegistered()
    {
        using var dir = new TempDir();
        var registry = new TextureRegistry(dir.Path);

        registry.Register("", new StandardMaterial3D());

        Assert.Equal(0, registry.PendingKeyCount);
        Assert.Equal("", registry.MaterialIdentity(""));
    }

    // Aliasing is deliberately NOT Register. Re-deduplication points a surface at another key's material,
    // and if that key's texture has already been applied it is done with — re-registering it would
    // resurrect a pending entry nothing will ever drain, and the load would never report itself finished.
    [Test]
    public void AliasingAnAlreadyAppliedKeyDoesNotResurrectIt()
    {
        using var dir = new TempDir();
        WriteTexture(dir.Path, "stone", 128, 128, 128);
        var registry = new TextureRegistry(dir.Path);
        registry.Register("stone", new StandardMaterial3D());
        registry.Apply("stone");
        Assert.Equal(0, registry.PendingKeyCount);

        registry.RegisterAlias("stone", new StandardMaterial3D());

        Assert.Equal(0, registry.PendingKeyCount);
    }

    // Aliasing onto a key that is still waiting DOES join the queue, which is the case it exists for.
    [Test]
    public void AliasingAKeyThatIsStillWaitingJoinsItsQueue()
    {
        using var dir = new TempDir();
        WriteTexture(dir.Path, "wood", 80, 60, 40);
        var registry = new TextureRegistry(dir.Path);
        var original = new StandardMaterial3D();
        var late = new StandardMaterial3D();
        registry.Register("wood", original);

        registry.RegisterAlias("wood", late);
        Assert.True(registry.Apply("wood"));

        Assert.NotNull(late.AlbedoTexture);
    }

    // Unturned's tiny palette textures are point-filtered, because the mesh UVs park on solid texels and
    // bilinear sampling bleeds neighbouring palette colours into smears — a stop sign's face mottles.
    // The .tex carries the filter mode, and it has to survive all the way onto the material.
    [Test]
    public void APointFilteredTextureArrivesWithNearestSampling()
    {
        using var dir = new TempDir();
        WritePointFiltered(dir.Path, "palette");
        var registry = new TextureRegistry(dir.Path);
        var standard = new StandardMaterial3D();
        registry.Register("palette", standard);

        Assert.True(registry.Apply("palette"));

        Assert.Equal(BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps, standard.TextureFilter);
    }

    // The foliage cutout family is a ShaderMaterial rather than a StandardMaterial3D, so it takes its
    // texture through shader parameters — and gets its own nearest-sampling variant of the shader.
    [Test]
    public void ACutoutShaderMaterialIsTexturedThroughItsParameters()
    {
        using var dir = new TempDir();
        WritePointFiltered(dir.Path, "leaves");
        var registry = new TextureRegistry(dir.Path);
        var cutout = new ShaderMaterial { Shader = ModelLibrary.CutoutVariant(nearest: false, EShaderCull.Back) };
        registry.Register("leaves", cutout);

        // Registered and drawable, but not yet textured: this is the window the whole cold load spends,
        // and the sampler really being unassigned here is what makes `hint_default_white` load-bearing
        // rather than decorative. ModelLibraryTests pins the hint; this pins the state it covers.
        Assert.Null(cutout.GetShaderParameter("albedo_texture").As<Texture2D>());

        Assert.True(registry.Apply("leaves"));

        Assert.NotNull(cutout.GetShaderParameter("albedo_texture").As<Texture2D>());
    }

    // The warm-load catch-up: everything whose .tex is already on disk goes on in one pass, rather than
    // waiting for the streamer to walk them one frame at a time.
    [Test]
    public void EverythingAlreadyOnDiskCanBeAppliedInOnePass()
    {
        using var dir = new TempDir();
        WriteTexture(dir.Path, "a", 1, 1, 1);
        WriteTexture(dir.Path, "b", 2, 2, 2);
        var registry = new TextureRegistry(dir.Path);
        registry.Register("a", new StandardMaterial3D());
        registry.Register("b", new StandardMaterial3D());
        registry.Register("never-decoded", new StandardMaterial3D());

        int applied = registry.ApplyAllAvailable();

        Assert.Equal(2, applied);
        Assert.Equal(1, registry.PendingKeyCount); // the undecodable one stays queued
    }

    // The load's bookkeeping is dropped once the world is textured. It is tens of thousands of strings
    // for a map like Germany, and holding them for the session is memory nothing reads again.
    [Test]
    public void TheLoadingIndexesAreReleasedWhenTheLoadIsDone()
    {
        using var dir = new TempDir();
        WriteTexture(dir.Path, "a", 1, 1, 1);
        var registry = new TextureRegistry(dir.Path);
        registry.Register("a", new StandardMaterial3D());
        registry.MaterialIdentity("a");
        registry.Apply("a");

        int released = registry.ReleaseLoadingIndexes();

        Assert.True(released > 0, "nothing was released at all");
        Assert.Equal(0, registry.PendingKeyCount);
        Assert.Empty(registry.MaterialIdentities);
        Assert.Empty(registry.ProvisionalIdentityKeys);
    }

    // The cache directory travels with the registry: the texture phase needs to know where to write, and
    // a registry pointed at the wrong directory would decode nothing while reporting no error.
    [Test]
    public void TheRegistryRemembersItsCacheDirectory()
    {
        using var dir = new TempDir();
        Assert.Equal(dir.Path, new TextureRegistry(dir.Path).TextureCacheDir);
    }

    // Asking twice does not re-resolve: the identity is a file hash, and hashing every .tex again per
    // material would be a second full pass over the texture cache.
    [Test]
    public void AnIdentityIsResolvedOnceAndRemembered()
    {
        using var dir = new TempDir();
        WriteTexture(dir.Path, "once", 9, 9, 9);
        var registry = new TextureRegistry(dir.Path);

        string first = registry.MaterialIdentity("once");
        File.Delete(Path.Combine(dir.Path, "once.tex")); // gone from disk now

        Assert.Equal(first, registry.MaterialIdentity("once")); // still answered, from memory
    }

    // A .tex the extraction worker is still writing must not be read.
    //
    // The window is real and it is on the path every first-time player takes: ObjectStreamer calls
    // ApplyAllAvailable from the main thread the moment the meshes land, while its worker is still
    // creating .tex files with a non-atomic File.Create — and ApplyAllAvailable sweeps EVERY pending key,
    // not only the ones the worker has signalled. The magic is the first four bytes written, so a
    // half-written file passes TextureCache.IsCurrent; TextureCache.Read then hands back a byte[] shorter
    // than its declared length, because BinaryReader.ReadBytes does not throw on a short read, and
    // Image.CreateFromData rejects it with engine error spam.
    //
    // The `.source` sidecar is the proof of commit: the extraction writes it atomically AFTER the
    // payload, exactly so this is answerable.
    [Test]
    public void AHalfWrittenTextureIsRefusedUntilItsWriteIsCommitted()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "streaming.tex");
        WriteTexture(dir.Path, "streaming", 20, 40, 60);

        // What the worker's File.Create leaves visible mid-write: a valid header, a truncated payload.
        byte[] complete = File.ReadAllBytes(path);
        File.Delete(TextureCache.SourcePath(path));
        File.WriteAllBytes(path, complete[..(complete.Length - 2)]);
        Assert.True(TextureCache.IsCurrent(path)); // the magic alone still says yes

        var registry = new TextureRegistry(dir.Path);
        var material = new StandardMaterial3D();
        registry.Register("streaming", material);

        Assert.False(registry.Apply("streaming"));
        Assert.Null(material.AlbedoTexture);
        // Refused, not written off: the key stays pending so the next frame's sweep picks it up. Caching
        // the miss is what made an earlier version of this poison the texture for the whole session.
        Assert.Equal(1, registry.PendingKeyCount);

        // The worker finishes: the payload lands and the sidecar commits it.
        File.WriteAllBytes(path, complete);
        TextureCache.RecordSource(path, Path.Combine(dir.Path, "some.masterbundle"), stamp: 1);

        Assert.True(registry.Apply("streaming"));
        Assert.NotNull(material.AlbedoTexture);
    }

    // --- helpers -------------------------------------------------------------------------------------

    // A Unity FilterMode.Point texture: the tiny palettes whose UVs park on solid texels.
    private static void WritePointFiltered(string dir, string key)
    {
        using var ms = new MemoryStream();
        TextureCache.Write(ms, new CachedTexture(4, 1, 1, 1, new byte[] { 200, 30, 30, 255 }, filterMode: 0));
        Commit(dir, key, ms.ToArray());
    }

    private static void WriteTexture(string dir, string key, byte r, byte g, byte b)
    {
        using var ms = new MemoryStream();
        TextureCache.Write(ms, new CachedTexture(4, 1, 1, 1, new byte[] { r, g, b, 255 }));
        Commit(dir, key, ms.ToArray());
    }

    // Payload then sidecar, in that order — a cache entry the extraction has finished writing, which is
    // what the registry admits. A fixture that wrote only the payload would be modelling a .tex still in
    // flight, and would then be testing the registry against a state production never leaves behind.
    private static void Commit(string dir, string key, byte[] payload)
    {
        string path = Path.Combine(dir, key + ".tex");
        File.WriteAllBytes(path, payload);
        TextureCache.RecordSource(path, Path.Combine(dir, "some.masterbundle"), stamp: 1);
    }

    private sealed class TempDir : System.IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "unturned-godot-textures-" + System.Guid.NewGuid().ToString("N"));

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
