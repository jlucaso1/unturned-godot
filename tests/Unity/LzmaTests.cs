using System;
using System.IO;
using System.Linq;
using SharpCompress.Compressors.LZMA;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class LzmaTests
{
    // Produces a UnityFS-style LZMA blob: 5-byte properties header followed by the raw stream.
    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        var props = new LzmaEncoderProperties();
        byte[] header;
        using (var encoder = new LzmaStream(props, false, output))
        {
            header = encoder.Properties;
            encoder.Write(data, 0, data.Length);
        }
        return header.Concat(output.ToArray()).ToArray();
    }

    [Fact]
    public void DecompressesFully()
    {
        byte[] original = System.Text.Encoding.ASCII.GetBytes(new string('x', 500) + "tail");
        byte[] compressed = Compress(original);
        byte[] result = Lzma.Decompress(compressed, original.Length, original.Length);
        Assert.Equal(original, result);
    }

    [Fact]
    public void DecompressesPrefixOnly()
    {
        byte[] original = System.Text.Encoding.ASCII.GetBytes(new string('a', 300) + new string('b', 300));
        byte[] compressed = Compress(original);
        byte[] result = Lzma.Decompress(compressed, original.Length, 100);
        Assert.Equal(100, result.Length);
        Assert.All(result, b => Assert.Equal((byte)'a', b));
    }

    [Fact]
    public void StopsWhenStreamEndsBeforeRequested()
    {
        byte[] original = System.Text.Encoding.ASCII.GetBytes("short");
        byte[] compressed = Compress(original);
        // Ask for more than exists: the read loop breaks and the tail stays zero.
        byte[] result = Lzma.Decompress(compressed, original.Length, original.Length + 10);
        Assert.Equal(original, result.Take(original.Length));
        Assert.Equal(new byte[10], result.Skip(original.Length).ToArray());
    }
}
