using System;
using System.Collections.Generic;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

// The Crunch decoder against textures Unity's own crunch compressor produced.
//
// CrunchCodecTests and CrunchTextureTests both run entirely on containers tests/Helpers/CrnBuilder.cs
// writes, which checks the decoder against this repo's own understanding of the format and no further —
// the two most intricate decoders in the project (this and m_CompressedMesh) had no byte-level check
// against Unity at all. The invariants below are ones a self-built fixture cannot satisfy by accident:
// the CRN header's dimensions have to match what the Texture2D beside it says, and the blocks that come
// out have to be exactly as many as those dimensions call for over the mip chain the header declares.
[Trait("Category", "RealData")]
public class RealCrunchTextureTests
{
    private const int Texture2DClassId = 28;
    private const int Dxt1Crunched = 28;
    private const int Dxt5Crunched = 29;
    private const int Dxt1 = 10;
    private const int Dxt5 = 12;

    [RealDataFact(RequiresMasterBundle = true)]
    public void ShippedCrunchedTextures_UnwrapToTheBlocksTheirHeaderDescribes()
    {
        List<UnityTexture> crunched = CrunchedTextures();
        Assert.NotEmpty(crunched);

        int decoded = 0;
        foreach ((int index, byte[] pixels) in StreamedPixels(crunched))
        {
            UnityTexture texture = crunched[index];
            CachedTexture cached = CachedTexture.From(texture, pixels);

            // A container this decoder cannot read comes back untouched, which is the one outcome that
            // would make every assertion below vacuous.
            Assert.True(cached.Format is Dxt1 or Dxt5,
                $"{texture.Name}: crunched format {texture.Format} did not unwrap (still {cached.Format})");
            Assert.Equal(texture.Format == Dxt1Crunched ? Dxt1 : Dxt5, cached.Format);

            // The CRN header is the authority on the decoded size, and it has to agree with the
            // Texture2D that points at it.
            Assert.Equal(texture.Width, cached.Width);
            Assert.Equal(texture.Height, cached.Height);
            Assert.InRange(cached.MipCount, 1, texture.MipCount);

            Assert.Equal(BlockBytes(cached.Width, cached.Height, cached.MipCount, cached.Format),
                cached.Pixels.Length);
            decoded++;
        }

        Assert.Equal(crunched.Count, decoded);
    }

    // Every crunched Texture2D in the masterbundle, in the order the file lists them.
    private static List<UnityTexture> CrunchedTextures()
    {
        SerializedFile file = GameData.Prefabs.File;
        var textures = new List<UnityTexture>();
        foreach (SerializedObject o in file.Objects)
        {
            if (o.ClassId != Texture2DClassId)
                continue;
            UnityTexture texture = UnityTexture.Read(TypeTreeReader.Read(o.TypeTree, file.ReaderFor(o)));
            if (texture.Format is Dxt1Crunched or Dxt5Crunched)
                textures.Add(texture);
        }
        return textures;
    }

    // Their pixel ranges, pulled out of the bundle's .resS in the single forward pass the format allows —
    // the LZMA block cannot seek backwards, so this is the same plan a real load walks.
    private static IEnumerable<(int Index, byte[] Pixels)> StreamedPixels(List<UnityTexture> textures)
    {
        using MasterBundleStream? stream = MasterBundleStream.OpenFile(GameData.MasterBundle!);
        Assert.NotNull(stream);

        var nodes = new List<BundlePass.Node>();
        foreach (MasterBundleStream.Node node in stream!.Nodes)
            nodes.Add(new BundlePass.Node(FileNameOf(node.Path), node.Size));

        var wants = new List<BundlePass.Want>();
        for (int i = 0; i < textures.Count; i++)
        {
            UnityTexture texture = textures[i];
            Assert.True(texture.StreamSize > 0,
                $"{texture.Name}: a crunched texture with no streamed pixels to read");
            wants.Add(new BundlePass.Want(FileNameOf(texture.StreamPath), texture.StreamOffset,
                texture.StreamSize, i));
        }

        var results = new List<(int, byte[])>();
        foreach (BundlePass.Step step in BundlePass.Plan(nodes, wants))
        {
            SeekToNode(stream, nodes, step.Node);
            ForwardRegions.Read((buffer, offset, count) => stream.Read(buffer, offset, count),
                step.ReadTo, step.Regions, (index, bytes) => results.Add((index, bytes)));
        }
        return results;
    }

    // The stream is consumed strictly front to back, so reaching a node means draining everything before
    // it that the plan did not already read.
    private static void SeekToNode(MasterBundleStream stream, List<BundlePass.Node> nodes, int node)
    {
        long start = 0;
        for (int i = 0; i < node; i++)
            start += nodes[i].Size;

        var scratch = new byte[1 << 20];
        while (stream.Cursor < start)
        {
            int want = (int)Math.Min(scratch.Length, start - stream.Cursor);
            if (stream.Read(scratch, 0, want) != want)
                break;
        }
    }

    private static string FileNameOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    // How many bytes a full DXT mip chain of these dimensions occupies: 4x4 blocks, 8 bytes each for
    // DXT1 and 16 for DXT5, with every level rounded up to a whole block and never smaller than one.
    private static int BlockBytes(int width, int height, int levels, int format)
    {
        int perBlock = format == Dxt1 ? 8 : 16;
        int total = 0;
        for (int level = 0; level < levels; level++)
        {
            int w = Math.Max(1, width >> level);
            int h = Math.Max(1, height >> level);
            total += ((w + 3) / 4) * ((h + 3) / 4) * perBlock;
        }
        return total;
    }
}
