using System.IO;

namespace UnturnedGodot.Unity;

public readonly struct CachedTexture
{
    public readonly int Format; // Unity TextureFormat enum
    public readonly int Width;
    public readonly int Height;
    public readonly int MipCount;
    public readonly byte[] Pixels;

    public CachedTexture(int format, int width, int height, int mipCount, byte[] pixels)
    {
        Format = format;
        Width = width;
        Height = height;
        MipCount = mipCount;
        Pixels = pixels;
    }
}

// Compact on-disk format for an extracted texture (Unity format + dimensions + raw block bytes),
// deduplicated by texture key so shared palette textures are stored once.
public static class TextureCache
{
    private const uint Magic = 0x32584754; // "TGX2"

    public static void Write(Stream stream, CachedTexture texture)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(texture.Format);
        w.Write(texture.Width);
        w.Write(texture.Height);
        w.Write(texture.MipCount);
        w.Write(texture.Pixels.Length);
        w.Write(texture.Pixels);
    }

    public static CachedTexture Read(Stream stream)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != Magic)
            throw new InvalidDataException("Not a texture cache stream");

        int format = r.ReadInt32();
        int width = r.ReadInt32();
        int height = r.ReadInt32();
        int mipCount = r.ReadInt32();
        int length = r.ReadInt32();
        return new CachedTexture(format, width, height, mipCount, r.ReadBytes(length));
    }
}
