using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace UnturnedGodot.Unity;

public readonly struct CachedTexture
{
    public readonly int Format; // Unity TextureFormat enum
    public readonly int Width;
    public readonly int Height;
    public readonly int MipCount;
    public readonly int FilterMode; // Unity FilterMode: 0 = Point (palette textures) else bilinear/trilinear
    public readonly byte[] Pixels;

    public CachedTexture(int format, int width, int height, int mipCount, byte[] pixels, int filterMode = 1)
    {
        Format = format;
        Width = width;
        Height = height;
        MipCount = mipCount;
        FilterMode = filterMode;
        Pixels = pixels;
    }

    // Unity TextureFormat values that wrap DXT blocks in a Crunch container, and what they hold.
    private const int Dxt1Crunched = 28;
    private const int Dxt5Crunched = 29;
    private const int Dxt1 = 10;
    private const int Dxt5 = 12;

    // The cache entry for a texture read out of a bundle, with any Crunch container unwrapped so the rest
    // of the pipeline only ever sees plain DXT blocks.
    public static CachedTexture From(UnityTexture texture, byte[] pixels) =>
        Decoded(new CachedTexture(texture.Format, texture.Width, texture.Height, texture.MipCount,
            pixels, texture.FilterMode));

    // Unwraps a crunched entry into the DXT blocks it holds. Anything else, including a container this
    // decoder cannot read, is returned untouched: the renderer already treats an unknown format as "no
    // texture" rather than failing the load. Cache entries written before the decoder existed still hold
    // the container, so this also runs when one is read back.
    public static CachedTexture Decoded(CachedTexture cached)
    {
        if (cached.Format is not (Dxt1Crunched or Dxt5Crunched)
            || !CrunchTexture.TryDecode(cached.Pixels, out byte[] blocks,
                out CrunchTexture.BlockFormat format, out int width, out int height, out int levels))
        {
            return cached;
        }

        // The CRN header is the authority on the decoded size: the Texture2D's own mip count can name
        // levels the crunched stream does not carry.
        return new CachedTexture(format == CrunchTexture.BlockFormat.Dxt1 ? Dxt1 : Dxt5,
            width, height, levels, blocks, cached.FilterMode);
    }
}

// Compact on-disk format for an extracted texture (Unity format + dimensions + raw block bytes),
// deduplicated by texture key so shared palette textures are stored once.
public static class TextureCache
{
    private const uint Magic = 0x33584754; // "TGX3": now carries the Unity filter mode
    private const uint SourceMagic = 0x31534754; // "TGS1"

    // True when the file starts with the current format magic; false for stale formats, short files or a
    // missing path — stale caches are rewritten by the next texture pass instead of crashing Read.
    public static bool IsCurrent(string path)
    {
        try
        {
            using FileStream s = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return s.Read(head) == 4 &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(head) == Magic;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // Texture keys are stable across updates because Unity PathIDs usually stay stable. Current cache
    // magic therefore proves only that the payload is readable, not that its pixels came from the current
    // bundle revision. The source sidecar is committed after the texture payload; an absent, interrupted,
    // or stale sidecar safely forces that one texture to be extracted again.
    public static bool IsCurrentForSource(string path, string bundlePath, long stamp)
    {
        if (!IsCurrent(path))
            return false;
        try
        {
            byte[] data = File.ReadAllBytes(SourcePath(path));
            if (data.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(data) != SourceMagic
                || BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(4)) != stamp)
                return false;
            int length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
            if (length < 0 || 16L + length != data.Length)
                return false;
            string recorded = Encoding.UTF8.GetString(data, 16, length);
            return string.Equals(recorded, Path.GetFullPath(bundlePath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }

    public static void RecordSource(string path, string bundlePath, long stamp)
    {
        string owner = SourcePath(path);
        string temporary = owner + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] source = Encoding.UTF8.GetBytes(Path.GetFullPath(bundlePath));
            var data = new byte[16 + source.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(data, SourceMagic);
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(4), stamp);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), source.Length);
            source.CopyTo(data.AsSpan(16));
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, owner, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            // Best effort. Without the sidecar the next load safely rewrites the texture.
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public static string SourcePath(string texturePath) => texturePath + ".source";

    public static void Remove(string texturePath)
    {
        foreach (string candidate in new[] { texturePath, SourcePath(texturePath) })
            try { File.Delete(candidate); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static void Write(Stream stream, CachedTexture texture)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(texture.Format);
        w.Write(texture.Width);
        w.Write(texture.Height);
        w.Write(texture.MipCount);
        w.Write(texture.FilterMode);
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
        int filterMode = r.ReadInt32();
        int length = r.ReadInt32();
        return new CachedTexture(format, width, height, mipCount, r.ReadBytes(length), filterMode);
    }
}
