using System;
using System.Collections.Generic;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class MapBundleTests
{
    private static byte[] Pixels(int count)
    {
        var pixels = new byte[count];
        for (int i = 0; i < count; i++)
            pixels[i] = (byte)(i + 1);
        return pixels;
    }

    private static List<(UnityTexture Texture, byte[] Pixels)> ReadTextures(MapBundle bundle)
    {
        var found = new List<(UnityTexture, byte[])>();
        foreach (SerializedObject o in bundle.Objects)
            if (bundle.TryReadTexture(o, out UnityTexture texture, out byte[] pixels))
                found.Add((texture, pixels));
        return found;
    }

    [Fact]
    public void Read_UnityRawContainer_DecodesInlineTextures()
    {
        byte[] first = Pixels(16);
        byte[] second = Pixels(4);
        MapBundle bundle = MapBundle.Read(new MapBundleBuilder()
            .Add("Highway_0", 2, 2, first)
            .Add("Trail", 1, 1, second)
            .Build())!;

        List<(UnityTexture Texture, byte[] Pixels)> textures = ReadTextures(bundle);

        Assert.Equal(2, textures.Count);
        Assert.Equal("Highway_0", textures[0].Texture.Name);
        Assert.Equal(2, textures[0].Texture.Width);
        Assert.Equal(first, textures[0].Pixels);
        Assert.Equal("Trail", textures[1].Texture.Name);
        Assert.Equal(second, textures[1].Pixels);
    }

    [Fact]
    public void Read_UnityFsContainer_DecodesInlineTextures()
    {
        byte[] pixels = Pixels(16);
        MapBundle bundle = MapBundle.Read(new MapBundleBuilder { UnityFs = true }
            .Add("Highway_0", 2, 2, pixels)
            .Build())!;

        (UnityTexture texture, byte[] decoded) = Assert.Single(ReadTextures(bundle));
        Assert.Equal("Highway_0", texture.Name);
        Assert.Equal(pixels, decoded);
    }

    [Fact]
    public void Read_UnityFsContainer_ResolvesPixelsFromTheStreamEntry()
    {
        // Germany's road bundle: the SerializedFile carries no pixels, only an "archive:/CAB-x/CAB-x.resS"
        // reference to the entry beside it.
        byte[] first = Pixels(16);
        byte[] second = Pixels(8);
        MapBundle bundle = MapBundle.Read(new MapBundleBuilder { UnityFs = true, StreamedPixels = true }
            .Add("Highway_0", 2, 2, first)
            .Add("Highway_1", 2, 1, second)
            .Build())!;

        List<(UnityTexture Texture, byte[] Pixels)> textures = ReadTextures(bundle);

        Assert.Equal(2, textures.Count);
        Assert.Equal(first, textures[0].Pixels);   // each one slices its own span out of the stream
        Assert.Equal(second, textures[1].Pixels);
    }

    [Fact]
    public void TryReadTexture_MissingStreamEntry_ReportsNoPixels()
    {
        // The .resS the textures point at is absent (a truncated bundle): the objects still parse, they
        // simply have nothing to draw with, which is not the same as the bundle failing to open.
        MapBundle bundle = MapBundle.Read(new MapBundleBuilder
        {
            UnityFs = true,
            StreamedPixels = true,
            StreamName = "CAB-elsewhere.resS",
        }.Add("Highway_0", 2, 2, Pixels(16)).Build())!;

        // Rebuild without the stream entry by reading a bundle whose only file is the SerializedFile.
        MapBundle withoutStream = MapBundle.Read(RawBundleBytes.Wrap("CAB-map", SerializedFileOf(bundle)))!;

        Assert.Empty(ReadTextures(withoutStream));
        Assert.NotEmpty(withoutStream.Objects);
    }

    [Fact]
    public void TryReadTexture_NonTextureObject_IsSkipped()
    {
        MapBundle bundle = MapBundle.Read(new SerializedFileBuilder { Version = 15, ClassId = 142 }.Build()
            is var serialized ? RawBundleBytes.Wrap("CAB-map", serialized) : throw new InvalidOperationException())!;

        SerializedObject obj = Assert.Single(bundle.Objects);
        Assert.False(bundle.TryReadTexture(obj, out _, out byte[] pixels));
        Assert.Empty(pixels);
    }

    [Fact]
    public void Read_BundleWithOnlyStreamEntries_IsNull() =>
        Assert.Null(MapBundle.Read(new UnityFsBuilder().Add("CAB-map.resS", new byte[] { 1, 2 }).Build()));

    [Fact]
    public void ReadFile_MissingPath_IsNull() =>
        Assert.Null(MapBundle.ReadFile("/no/such/Roads.unity3d"));

    [Fact]
    public void ReadFile_ReadsFromDisk()
    {
        using var dir = new TempDir();
        string path = dir.Write("Roads.unity3d", new MapBundleBuilder().Add("Trail", 1, 1, Pixels(4)).Build());

        MapBundle bundle = MapBundle.ReadFile(path)!;
        Assert.Equal("Trail", Assert.Single(ReadTextures(bundle)).Texture.Name);
    }

    // The bundle's SerializedFile bytes, so a test can re-wrap it without the entries beside it.
    private static byte[] SerializedFileOf(MapBundle bundle)
    {
        SerializedObject first = bundle.Objects[0];
        UnityBinaryReader reader = bundle.File.ReaderFor(first);
        // ReaderFor exposes the whole file the objects were read out of; ask it for every byte.
        reader.Position = 0;
        return reader.ReadBytes(reader.Length);
    }
}
