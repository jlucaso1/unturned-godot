using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

// Reading named textures out of a bundle, which is how every terrain layer and impact decal is fetched.
//
// The subject is the ADDRESSING, not the pixels: a texture is asked for by the container path the bundle
// publishes it under, and the three readers differ only in what they are willing to pay to find it —
// the SerializedFile alone, a forward pass that stops when nothing is still owed, or the whole blob.
// A path that resolves to the wrong asset, or to nothing, is a surface that loads untextured with no
// error anywhere, so what these hold is that all three agree about which bytes belong to which name.
[Collection(ProcessStateCollection.Name)]
public class BundleTexturesTests
{
    private static byte[] Pixels(byte seed, int length = 64)
    {
        var pixels = new byte[length];
        for (int i = 0; i < length; i++)
            pixels[i] = (byte)(seed + i);
        return pixels;
    }

    [Fact]
    public void LocateResolvesAContainerPathToItsTexture()
    {
        var builder = new AssetFileBuilder();
        builder.AddInlineTexture("assets/test/grass.png", "grass", 4, 4, Pixels(1));
        builder.AddInlineTexture("assets/test/sand.png", "sand", 8, 8, Pixels(50));
        SerializedFile file = SerializedFile.Read(builder.BuildSerializedFile());

        var found = new Dictionary<string, UnityTexture>(StringComparer.Ordinal);
        foreach ((string path, UnityTexture texture) in
            BundleTextures.Locate(file, new[] { "assets/test/grass.png", "assets/test/sand.png" }))
        {
            found[path] = texture;
        }

        Assert.Equal(2, found.Count);
        Assert.Equal("grass", found["assets/test/grass.png"].Name);
        Assert.Equal(4, found["assets/test/grass.png"].Width);
        Assert.Equal("sand", found["assets/test/sand.png"].Name);
        Assert.Equal(8, found["assets/test/sand.png"].Height);
    }

    // Only what was asked for. The core bundle publishes tens of thousands of containers and a caller
    // wants a handful, so a reader that returned the rest would hand back a hundred megabytes of pixels
    // nobody has a use for.
    [Fact]
    public void LocateIgnoresContainerPathsNobodyAskedFor()
    {
        var builder = new AssetFileBuilder();
        builder.AddInlineTexture("assets/test/grass.png", "grass", 4, 4, Pixels(1));
        builder.AddInlineTexture("assets/test/sand.png", "sand", 4, 4, Pixels(2));
        SerializedFile file = SerializedFile.Read(builder.BuildSerializedFile());

        var paths = new List<string>();
        foreach ((string path, UnityTexture _) in
            BundleTextures.Locate(file, new[] { "assets/test/sand.png" }))
        {
            paths.Add(path);
        }

        Assert.Equal(new[] { "assets/test/sand.png" }, paths);
    }

    // One asset published under two names is a shape the game ships (a holiday variant reuses a texture),
    // and each name has to answer with it.
    [Fact]
    public void LocateAnswersEveryNameOneAssetIsPublishedUnder()
    {
        var builder = new AssetFileBuilder();
        long id = builder.AddInlineTexture("assets/test/grass.png", "grass", 4, 4, Pixels(3));
        builder.Alias("assets/test/grass_alt.png", id);
        SerializedFile file = SerializedFile.Read(builder.BuildSerializedFile());

        var names = new List<string>();
        foreach ((string path, UnityTexture texture) in BundleTextures.Locate(file,
            new[] { "assets/test/grass.png", "assets/test/grass_alt.png" }))
        {
            names.Add(path);
            Assert.Equal("grass", texture.Name);
        }

        Assert.Equal(2, names.Count);
    }

    // A container entry pointing at something that is not a Texture2D is skipped rather than cast. A
    // workshop bundle that names a mesh where a texture is expected must not take the load with it.
    [Fact]
    public void LocateSkipsAContainerEntryThatIsNotATexture()
    {
        var builder = new AssetFileBuilder();
        builder.AddAudioClip("assets/test/notatexture.png", "clip", new byte[] { 1, 2, 3, 4 });
        SerializedFile file = SerializedFile.Read(builder.BuildSerializedFile());

        Assert.Empty(BundleTextures.Locate(file, new[] { "assets/test/notatexture.png" }));
    }

    [Fact]
    public void ExtractInlineKeepsThePixelsThatAreInTheSerializedFile()
    {
        var builder = new AssetFileBuilder();
        builder.AddInlineTexture("assets/test/grass.png", "grass", 4, 4, Pixels(7));
        SerializedFile file = SerializedFile.Read(builder.BuildSerializedFile());

        Dictionary<string, CachedTexture> inline =
            BundleTextures.ExtractInline(file, new[] { "assets/test/grass.png" });

        CachedTexture texture = Assert.Contains("assets/test/grass.png", inline);
        Assert.Equal(4, texture.Width);
        Assert.Equal(4, texture.Height);
        Assert.NotEmpty(texture.Pixels);
    }

    // A streamed texture has no inline bytes, so the SerializedFile alone cannot serve it. Returning it
    // with an empty pixel buffer would cache a blank texture over the real one, permanently.
    [Fact]
    public void ExtractInlineLeavesAStreamedTextureAlone()
    {
        var builder = new AssetFileBuilder();
        builder.AddStreamedTexture("assets/test/big.png", "big", 16, 16, Pixels(9, 256));
        SerializedFile file = SerializedFile.Read(builder.BuildSerializedFile());

        Assert.Empty(BundleTextures.ExtractInline(file, new[] { "assets/test/big.png" }));
    }

    [Fact]
    public void ExtractStreamedServesPixelsOutOfTheStreamNode()
    {
        var builder = new AssetFileBuilder();
        builder.AddStreamedTexture("assets/test/big.png", "big", 16, 16, Pixels(11, 256));
        using var dir = new TempDir();
        string bundle = dir.Write("test.masterbundle", builder.BuildBundle());

        Dictionary<string, CachedTexture> extracted =
            BundleTextures.ExtractStreamed(bundle, new[] { "assets/test/big.png" });

        CachedTexture texture = Assert.Contains("assets/test/big.png", extracted);
        Assert.Equal(16, texture.Width);
        Assert.NotEmpty(texture.Pixels);
    }

    // Inline and streamed in one bundle: the forward pass has to take the first from the SerializedFile
    // and the second from the node after it, in one go.
    [Fact]
    public void ExtractStreamedTakesInlineAndStreamedTexturesInOnePass()
    {
        var builder = new AssetFileBuilder();
        builder.AddInlineTexture("assets/test/small.png", "small", 4, 4, Pixels(13));
        builder.AddStreamedTexture("assets/test/big.png", "big", 16, 16, Pixels(17, 256));
        using var dir = new TempDir();
        string bundle = dir.Write("test.masterbundle", builder.BuildBundle());

        Dictionary<string, CachedTexture> extracted = BundleTextures.ExtractStreamed(bundle,
            new[] { "assets/test/small.png", "assets/test/big.png" });

        Assert.Equal(2, extracted.Count);
        Assert.NotEmpty(extracted["assets/test/small.png"].Pixels);
        Assert.NotEmpty(extracted["assets/test/big.png"].Pixels);
    }

    // A bundle that is not the single-LZMA-block shape falls back to decoding the whole blob. The answer
    // has to be the same one; a fallback that returned less would silently untexture the workshop maps.
    [Fact]
    public void ExtractStreamedFallsBackToTheWholeBlobForAnotherBundleShape()
    {
        var builder = new AssetFileBuilder();
        builder.AddStreamedTexture("assets/test/big.png", "big", 16, 16, Pixels(19, 256));
        using var dir = new TempDir();
        string bundle = dir.Write("plain.masterbundle", builder.BuildBundle(singleLzmaBlock: false));

        Dictionary<string, CachedTexture> extracted =
            BundleTextures.ExtractStreamed(bundle, new[] { "assets/test/big.png" });

        CachedTexture texture = Assert.Contains("assets/test/big.png", extracted);
        Assert.Equal(16, texture.Width);
    }

    [Fact]
    public void ExtractAllServesInlineAndStreamedPixelsAlike()
    {
        var builder = new AssetFileBuilder();
        builder.AddInlineTexture("assets/test/small.png", "small", 4, 4, Pixels(23));
        builder.AddStreamedTexture("assets/test/big.png", "big", 16, 16, Pixels(29, 256));
        using var dir = new TempDir();
        string bundle = dir.Write("plain.masterbundle", builder.BuildBundle(singleLzmaBlock: false));

        Dictionary<string, CachedTexture> extracted = BundleTextures.ExtractAll(bundle,
            new[] { "assets/test/small.png", "assets/test/big.png" });

        Assert.Equal(2, extracted.Count);
        Assert.NotEmpty(extracted["assets/test/big.png"].Pixels);
    }

    // A stream node that sits in a subfolder of the bundle. A texture names its stream by the path Unity
    // wrote — "archive:/CAB-x/sub/CAB-x.resS" — while the bundle's node table keys it by its own path, so
    // the two only meet on the last segment. Both readers have to match on that, and a bundle laid out
    // this way is the case where matching on the whole string silently finds nothing.
    [Fact]
    public void AStreamNodeInASubfolderIsStillMatched()
    {
        var builder = new AssetFileBuilder { StreamName = "sub/CAB-test.resS" };
        builder.AddStreamedTexture("assets/test/big.png", "big", 16, 16, Pixels(37, 256));
        using var dir = new TempDir();
        string bundle = dir.Write("test.masterbundle", builder.BuildBundle());

        Assert.NotEmpty(BundleTextures.ExtractStreamed(bundle, new[] { "assets/test/big.png" })
            ["assets/test/big.png"].Pixels);
    }

    [Fact]
    public void TheWholeBlobReaderAlsoMatchesAStreamNodeInASubfolder()
    {
        var builder = new AssetFileBuilder { StreamName = "sub/CAB-test.resS" };
        builder.AddStreamedTexture("assets/test/big.png", "big", 16, 16, Pixels(41, 256));
        using var dir = new TempDir();
        string bundle = dir.Write("plain.masterbundle", builder.BuildBundle(singleLzmaBlock: false));

        Assert.NotEmpty(BundleTextures.ExtractAll(bundle, new[] { "assets/test/big.png" })
            ["assets/test/big.png"].Pixels);
    }

    // ...and a texture whose stream node the bundle simply does not carry resolves to nothing rather than
    // to whichever node happened to be there.
    [Fact]
    public void ATextureNamingAStreamNodeTheBundleLacksIsDropped()
    {
        var builder = new AssetFileBuilder();
        builder.AddStreamedTexture("assets/test/big.png", "big", 16, 16, Pixels(43, 256));
        using var dir = new TempDir();
        // Built without the stream entry: the SerializedFile still names it, so the texture is located
        // and then found to have no bytes anywhere.
        var fs = new UnityFsBuilder();
        fs.Add("CAB-test", builder.BuildSerializedFile());
        fs.Add("CAB-other.resource", new byte[] { 1, 2, 3, 4 });
        string bundle = dir.Write("plain.masterbundle", fs.Build());

        Assert.Empty(BundleTextures.ExtractAll(bundle, new[] { "assets/test/big.png" }));
    }

    // Asking for nothing costs nothing: neither reader may open the file at all, which is what lets a
    // caller plan unconditionally and pass an empty set when the cache is already complete.
    [Fact]
    public void AskingForNothingNeverOpensTheBundle()
    {
        string[] nothing = Array.Empty<string>();

        Assert.Empty(BundleTextures.ExtractStreamed("/nonexistent-bundle", nothing));
        Assert.Empty(BundleTextures.ExtractAll("/nonexistent-bundle", nothing));
    }

    // An unreadable bundle loses its textures rather than the level. The caller is mid-build and would
    // rather have an untextured object than no map.
    [Fact]
    public void AnUnreadableBundleAnswersEmptyRatherThanThrowing()
    {
        Assert.Empty(BundleTextures.ExtractStreamed("/nonexistent-bundle",
            new[] { "assets/test/grass.png" }));
        Assert.Empty(BundleTextures.ExtractAll("/nonexistent-bundle",
            new[] { "assets/test/grass.png" }));
    }

    // A path the bundle does not publish is simply absent. Both folder shapes of a decal are offered for
    // every effect precisely because only the bundle knows which exists, so this is the ordinary case.
    [Fact]
    public void AContainerPathTheBundleDoesNotHaveIsAbsentRatherThanAnError()
    {
        var builder = new AssetFileBuilder();
        builder.AddInlineTexture("assets/test/grass.png", "grass", 4, 4, Pixels(31));
        using var dir = new TempDir();
        string bundle = dir.Write("test.masterbundle", builder.BuildBundle());

        Assert.Empty(BundleTextures.ExtractStreamed(bundle, new[] { "assets/test/nothing-like-this.png" }));
    }

    // The unreadable-bundle path says so on stderr. It is the only trace a player has of why a surface
    // came out untextured, so it is worth a test of its own.
    [Fact]
    public void AnUnreadableBundleIsReportedToTheHost()
    {
        var log = new RecordingHostLog();
        IHostLog previous = HostLog.Sink;
        HostLog.Sink = log;
        try
        {
            BundleTextures.ExtractAll("/nonexistent-bundle", new[] { "assets/test/grass.png" });
        }
        finally
        {
            HostLog.Sink = previous;
        }

        Assert.Contains(log.Errors, line => line.Contains("nonexistent-bundle", StringComparison.Ordinal));
    }
}
