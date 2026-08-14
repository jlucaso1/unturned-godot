using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Assets;

// Pulling the impact decal textures out of a bundle, and — the harder half — knowing when that has
// already been done.
//
// Both of the obvious ways to answer "is this bundle's share already fetched?" are wrong, which is why
// there is a manifest at all. "Any wanted file exists" skips the bundle forever if extraction was
// interrupted after writing one, or if a game update adds a decal path beside a still-current one.
// "Every wanted file exists" never passes: most candidates are paths the bundle does not have, because
// both folder shapes are offered for every effect precisely because only the bundle knows which is real.
public class ImpactDecalExtractorTests
{
    private const string BundleTag = "core";

    private static byte[] Pixels(byte seed, int length = 64)
    {
        var pixels = new byte[length];
        for (int i = 0; i < length; i++)
            pixels[i] = (byte)(seed + i);
        return pixels;
    }

    private static ImpactDecalExtractor.Request RequestFor(string bundlePath, string cacheDir,
        params string[] paths) =>
        new(bundlePath, BundleTag, paths, cacheDir);

    [Fact]
    public void AnEmptyCacheHasSatisfiedNothing()
    {
        using var dir = new TempDir();

        Assert.False(ImpactDecalExtractor.IsSatisfied(
            RequestFor("/nowhere.masterbundle", dir.Path, "assets/test/blood.png")));
    }

    // The manifest, not the textures, is what says the work is done — so a request whose paths the bundle
    // simply did not have still reads as satisfied once it has been asked.
    [Fact]
    public void AManifestOverPathsTheBundleNeverHadStillCounts()
    {
        using var dir = new TempDir();
        ImpactDecalExtractor.Request request =
            RequestFor("/nowhere.masterbundle", dir.Path, "assets/test/absent.png");

        ImpactDecalExtractor.WriteManifest(request);

        Assert.True(ImpactDecalExtractor.IsSatisfied(request));
    }

    // A request that has grown — a game update adding a decal path — is a different request, and has to
    // run again rather than ride the old manifest.
    [Fact]
    public void AWidenedRequestIsNotSatisfiedByTheOldManifest()
    {
        using var dir = new TempDir();
        ImpactDecalExtractor.WriteManifest(RequestFor("/nowhere.masterbundle", dir.Path, "a.png"));

        Assert.False(ImpactDecalExtractor.IsSatisfied(
            RequestFor("/nowhere.masterbundle", dir.Path, "a.png", "b.png")));
    }

    // ...and one that has narrowed is also a different request. Same count, different members, is the
    // case a plain length check would let through.
    [Fact]
    public void ADifferentSetOfTheSameSizeIsNotSatisfied()
    {
        using var dir = new TempDir();
        ImpactDecalExtractor.WriteManifest(RequestFor("/nowhere.masterbundle", dir.Path, "a.png", "b.png"));

        Assert.False(ImpactDecalExtractor.IsSatisfied(
            RequestFor("/nowhere.masterbundle", dir.Path, "a.png", "c.png")));
    }

    // Each bundle keeps its own manifest: one source's extraction must not mark another's as done.
    [Fact]
    public void EachBundleTagKeepsItsOwnManifest()
    {
        using var dir = new TempDir();
        ImpactDecalExtractor.WriteManifest(
            new ImpactDecalExtractor.Request("/core.masterbundle", "core", new[] { "a.png" }, dir.Path));

        Assert.False(ImpactDecalExtractor.IsSatisfied(new ImpactDecalExtractor.Request(
            "/workshop.masterbundle", "workshop-1234", new[] { "a.png" }, dir.Path)));
    }

    // What the manifest CANNOT vouch for is the textures still being readable: a cache format change
    // invalidates them without touching it. Anything it claims produced something has to still be current.
    [Fact]
    public void AStaleTextureFileUnsatisfiesTheRequest()
    {
        using var dir = new TempDir();
        ImpactDecalExtractor.Request request =
            RequestFor("/nowhere.masterbundle", dir.Path, "assets/test/blood.png");
        ImpactDecalExtractor.WriteManifest(request);

        // A .tex under the key the manifest implies, whose contents are not a current cache file.
        string key = ImpactDecalPlan.CacheKey(BundleTag, "assets/test/blood.png");
        dir.Write(Path.GetFileName(ImpactDecalExtractor.PathFor(dir.Path, key)),
            new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        Assert.False(ImpactDecalExtractor.IsSatisfied(request));
    }

    [Fact]
    public void ExtractingWritesATextureAndAManifestAndThenReadsAsSatisfied()
    {
        var builder = new AssetFileBuilder();
        builder.AddInlineTexture("assets/test/blood.png", "blood", 4, 4, Pixels(1));
        using var dir = new TempDir();
        using var cache = new TempDir();
        string bundle = dir.Write("test.masterbundle", builder.BuildBundle());
        ImpactDecalExtractor.Request request =
            RequestFor(bundle, cache.Path, "assets/test/blood.png");

        int written = ImpactDecalExtractor.Extract(request);

        Assert.Equal(1, written);
        Assert.True(File.Exists(ImpactDecalExtractor.PathFor(cache.Path,
            ImpactDecalPlan.CacheKey(BundleTag, "assets/test/blood.png"))));
        Assert.True(ImpactDecalExtractor.IsSatisfied(request));
    }

    // Asking for a path the bundle does not carry costs nothing and is not an error: it is the ordinary
    // case, since both folder shapes are offered for every effect.
    [Fact]
    public void APathTheBundleDoesNotCarryWritesNoTextureButStillCompletes()
    {
        var builder = new AssetFileBuilder();
        builder.AddInlineTexture("assets/test/blood.png", "blood", 4, 4, Pixels(3));
        using var dir = new TempDir();
        using var cache = new TempDir();
        string bundle = dir.Write("test.masterbundle", builder.BuildBundle());
        ImpactDecalExtractor.Request request =
            RequestFor(bundle, cache.Path, "assets/test/nothing-like-this.png");

        Assert.Equal(0, ImpactDecalExtractor.Extract(request));
        Assert.True(ImpactDecalExtractor.IsSatisfied(request),
            "a request the bundle could not answer still counts as asked");
    }

    // An empty request never opens the bundle at all, which is what lets a caller plan unconditionally.
    [Fact]
    public void AnEmptyRequestNeverOpensTheBundle()
    {
        using var cache = new TempDir();

        Assert.Equal(0, ImpactDecalExtractor.Extract(
            new ImpactDecalExtractor.Request("/nonexistent", BundleTag, Array.Empty<string>(), cache.Path)));
        Assert.Equal(0, ImpactDecalExtractor.Extract(
            new ImpactDecalExtractor.Request(string.Empty, BundleTag, new[] { "a.png" }, cache.Path)));
    }

    // A bundle that cannot be read writes no textures. That is the same outcome as a surface naming no
    // effect, which is the intended one.
    //
    // It DOES still write a manifest, and this pins that rather than endorsing it. Extract's own comment
    // says a run that failed part-way leaves no manifest and is simply retried — but the reader beneath
    // it catches its own IO errors and answers empty, so the guard here never fires and "the bundle is
    // missing" is indistinguishable from "the bundle carries none of these". Worth knowing before anyone
    // relies on the retry: a bundle unreadable for one boot is marked done for every later one.
    [Fact]
    public void AnUnreadableBundleWritesNoTextures()
    {
        using var cache = new TempDir();
        ImpactDecalExtractor.Request request =
            RequestFor("/nonexistent-bundle", cache.Path, "assets/test/blood.png");

        Assert.Equal(0, ImpactDecalExtractor.Extract(request));
        Assert.False(File.Exists(ImpactDecalExtractor.PathFor(cache.Path,
            ImpactDecalPlan.CacheKey(BundleTag, "assets/test/blood.png"))));
    }

    // The streamer writes what its own forward pass already read, rather than reopening the bundle. The
    // file it lands on has to be the one the runtime later looks for, under the same key.
    [Fact]
    public void ATextureWrittenByThePassLandsUnderTheKeyTheRuntimeReads()
    {
        using var cache = new TempDir();
        ImpactDecalExtractor.Request request =
            RequestFor("/core.masterbundle", cache.Path, "assets/test/blood.png");
        var texture = CachedTexture.Decoded(new CachedTexture(4, 2, 2, 1, Pixels(9, 16)));

        ImpactDecalExtractor.WriteTexture(request, "assets/test/blood.png", texture);

        string path = ImpactDecalExtractor.PathFor(cache.Path,
            ImpactDecalPlan.CacheKey(BundleTag, "assets/test/blood.png"));
        Assert.True(File.Exists(path));
        Assert.True(TextureCache.IsCurrent(path));
    }

    // Per texture: one unwritable file must not cost the rest of them, and must not throw out through the
    // shared bundle pass this runs inside.
    //
    // The cache directory is unwritable because its PARENT IS A FILE, which is the one way to say that
    // portably: Directory.CreateDirectory throws IOException on a path whose parent is not a directory on
    // every platform, with no permission bits and no privileged-user escape involved. This used to point
    // at /proc, which is a Linux fact — on Windows it resolved to a perfectly writable C:\proc, the write
    // went through, and the assertion failed for a reason that had nothing to do with the extractor.
    [Fact]
    public void AnUnwritableCacheDirectoryIsSurvived()
    {
        using var temp = new TempDir();
        string blocker = Path.Combine(temp.Path, "not-a-directory");
        File.WriteAllBytes(blocker, Array.Empty<byte>());

        ImpactDecalExtractor.Request request = RequestFor("/core.masterbundle",
            Path.Combine(blocker, "unturned-godot-cannot-write-here"), "assets/test/blood.png");

        ImpactDecalExtractor.WriteTexture(request, "assets/test/blood.png",
            CachedTexture.Decoded(new CachedTexture(4, 2, 2, 1, Pixels(11, 16))));
        ImpactDecalExtractor.WriteManifest(request);

        Assert.False(ImpactDecalExtractor.IsSatisfied(request));
    }

    [Fact]
    public void ANullRequestIsRejectedRatherThanDereferenced()
    {
        Assert.Throws<ArgumentNullException>(() => ImpactDecalExtractor.IsSatisfied(null!));
        Assert.Throws<ArgumentNullException>(() => ImpactDecalExtractor.Extract(null!));
        Assert.Throws<ArgumentNullException>(() => ImpactDecalExtractor.WriteManifest(null!));
        Assert.Throws<ArgumentNullException>(() =>
            ImpactDecalExtractor.WriteTexture(null!, "a.png", default));
    }

    // The manifest and the textures are addressed independently, and both have to agree about the tag or
    // a texture is written where nothing later looks for it.
    [Fact]
    public void TheManifestAndTheTexturesAreNamedFromTheSameRequest()
    {
        using var cache = new TempDir();
        ImpactDecalExtractor.Request request =
            RequestFor("/core.masterbundle", cache.Path, "assets/test/blood.png");

        Assert.Equal(Path.Combine(cache.Path, $"decals_{BundleTag}.manifest"),
            ImpactDecalExtractor.ManifestFor(cache.Path, BundleTag));
        Assert.EndsWith(".tex", ImpactDecalExtractor.PathFor(cache.Path,
            ImpactDecalPlan.CacheKey(request.BundleTag, "assets/test/blood.png")), StringComparison.Ordinal);
    }
}
