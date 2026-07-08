using System.IO;
using SharpCompress.Compressors.LZMA;

namespace UnturnedGodot.Unity;

// Thin wrapper over SharpCompress's LZMA decoder. UnityFS LZMA blocks are a 5-byte properties
// header followed by the raw stream (no size field). The decoder must be initialized with the
// block's full size for correct framing, but we can stop after `wantBytes` — letting us read the
// SerializedFile prefix without inflating the whole 1.4 GB block.
public static class Lzma
{
    public static byte[] Decompress(byte[] data, int fullSize, int wantBytes)
    {
        byte[] properties = new byte[5];
        System.Array.Copy(data, 0, properties, 0, 5);

        using var input = new MemoryStream(data, 5, data.Length - 5);
        using var lzma = new LzmaStream(properties, input, data.Length - 5, fullSize);

        var output = new byte[wantBytes];
        int read = 0;
        while (read < wantBytes)
        {
            int n = lzma.Read(output, read, wantBytes - read);
            if (n <= 0)
                break;
            read += n;
        }
        return output;
    }
}
