using System;
using System.Collections.Generic;

namespace UnturnedGodot.Unity;

// A Texture2D read via TypeTreeReader. Pixel data is either inline ("image data") or in the
// bundle's .resS stream at m_StreamData.offset/size.
public sealed class UnityTexture
{
    public string Name = string.Empty;
    public int Width;
    public int Height;
    public int Format;      // Unity TextureFormat enum
    public int MipCount;
    public string StreamPath = string.Empty; // "archive:/.../<file>.resS", or empty when inline
    public long StreamOffset;
    public int StreamSize;
    public byte[] InlineData = Array.Empty<byte>();
    public int FilterMode = 1; // Unity FilterMode: 0 = Point (palette textures), 1 = Bilinear, 2 = Trilinear

    public static UnityTexture Read(Dictionary<string, object> tex)
    {
        int width = Convert.ToInt32(tex["m_Width"]);
        int height = Convert.ToInt32(tex["m_Height"]);
        var result = new UnityTexture
        {
            Name = tex.TryGetValue("m_Name", out object? n) ? (string)n : string.Empty,
            Width = width,
            Height = height,
            Format = Convert.ToInt32(tex["m_TextureFormat"]),
            MipCount = MipLevels(tex, width, height),
            InlineData = tex.TryGetValue("image data", out object? d) ? (byte[])d : Array.Empty<byte>(),
        };

        if (tex.TryGetValue("m_TextureSettings", out object? ts) && ts is Dictionary<string, object> settings &&
            settings.TryGetValue("m_FilterMode", out object? fm))
            result.FilterMode = Convert.ToInt32(fm);

        // Older textures (Unity 5.x per-map bundles) store pixels inline with no m_StreamData field.
        if (tex.TryGetValue("m_StreamData", out object? sd) && sd is Dictionary<string, object> stream)
        {
            result.StreamPath = (string)stream["path"];
            result.StreamOffset = Convert.ToInt64(stream["offset"]);
            result.StreamSize = Convert.ToInt32(stream["size"]);
        }
        return result;
    }

    // How many mip levels the pixel data holds. Unity 4's Texture2D (the pre-2018 per-map bundles) has no
    // m_MipCount: it stores a m_MipMap bool instead, and a chain is always complete down to 1x1, so the
    // count follows from the dimensions.
    private static int MipLevels(Dictionary<string, object> tex, int width, int height)
    {
        if (tex.TryGetValue("m_MipCount", out object? count))
            return Convert.ToInt32(count);
        if (!tex.TryGetValue("m_MipMap", out object? hasMips) || !Convert.ToBoolean(hasMips))
            return 1;

        int levels = 1;
        for (int w = width, h = height; w > 1 || h > 1; levels++)
        {
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }
        return levels;
    }

    // The last path segment of an "archive:/CAB-x/CAB-x.resS" reference is the bundle file name.
    public string StreamFileName
    {
        get
        {
            int slash = StreamPath.LastIndexOf('/');
            return slash >= 0 ? StreamPath[(slash + 1)..] : StreamPath;
        }
    }

    // Resolves the raw pixel bytes: from the .resS stream when present, otherwise the inline data.
    public byte[]? GetPixels(Func<string, byte[]?> resolveStreamFile)
    {
        if (StreamPath.Length == 0)
            return InlineData;

        byte[]? file = resolveStreamFile(StreamFileName);
        if (file == null || StreamOffset + StreamSize > file.Length)
            return null;

        var pixels = new byte[StreamSize];
        Array.Copy(file, StreamOffset, pixels, 0, StreamSize);
        return pixels;
    }
}
