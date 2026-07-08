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

    public static UnityTexture Read(Dictionary<string, object> tex)
    {
        var result = new UnityTexture
        {
            Name = tex.TryGetValue("m_Name", out object? n) ? (string)n : string.Empty,
            Width = Convert.ToInt32(tex["m_Width"]),
            Height = Convert.ToInt32(tex["m_Height"]),
            Format = Convert.ToInt32(tex["m_TextureFormat"]),
            MipCount = Convert.ToInt32(tex["m_MipCount"]),
            InlineData = tex.TryGetValue("image data", out object? d) ? (byte[])d : Array.Empty<byte>(),
        };

        // Older textures (Unity 5.x per-map bundles) store pixels inline with no m_StreamData field.
        if (tex.TryGetValue("m_StreamData", out object? sd) && sd is Dictionary<string, object> stream)
        {
            result.StreamPath = (string)stream["path"];
            result.StreamOffset = Convert.ToInt64(stream["offset"]);
            result.StreamSize = Convert.ToInt32(stream["size"]);
        }
        return result;
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
