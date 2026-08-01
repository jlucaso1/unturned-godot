using System;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

// The Crunch decoder, driven by hand-built .crn files (see CrnBuilder). Every model in them carries two
// one-bit symbols, so the exact bit sequence, and therefore the exact DXT output, is known up front.
public class CrunchTextureTests
{
    // The colour endpoint produced by six deltas of 1: c | b << 5 | a << 11 | f << 16 | e << 21 | d << 27.
    private const uint EndpointOfOnes = 0x08210821;

    // The two models a colour endpoint palette is prefixed with, then `count` entries of six deltas each.
    private static byte[] ColorEndpointStream(params bool[][] entries)
    {
        var w = new CrnBuilder.BitWriter();
        w.Model(2, 0, 1); // the 5-bit channels
        w.Model(2, 0, 1); // the 6-bit channels
        foreach (bool[] entry in entries)
            foreach (bool one in entry)
                w.Symbol(one);
        return w.ToArray();
    }

    // A selector palette of `count` entries, each eight symbols of the "no change" delta pair.
    private static byte[] SelectorStream(int count, int maxValue)
    {
        int unique = (maxValue * 2) + 1;
        int noChange = (maxValue * unique) + maxValue; // the index whose delta pair is (0, 0)

        var w = new CrnBuilder.BitWriter();
        w.Model(noChange + 1, 0, noChange);
        for (int i = 0; i < count; i++)
            for (int j = 0; j < 8; j++)
                w.Symbol(true);
        return w.ToArray();
    }

    private static byte[] AlphaEndpointStream(params bool[][] entries)
    {
        var w = new CrnBuilder.BitWriter();
        w.Model(2, 0, 1);
        foreach (bool[] entry in entries)
            foreach (bool one in entry)
                w.Symbol(one);
        return w.ToArray();
    }

    private static byte[] Tables(bool color, bool alpha)
    {
        var w = new CrnBuilder.BitWriter();
        w.Model(2, 0, 1); // chunk encodings: symbol 0 means "encoding 0, three times over"
        if (color)
        {
            w.Model(2, 0, 1); // colour endpoint delta
            w.Model(2, 0, 1); // colour selector delta
        }
        if (alpha)
        {
            w.Model(2, 0, 1); // alpha endpoint delta
            w.Model(2, 0, 1); // alpha selector delta
        }
        return w.ToArray();
    }

    // An 8x8 DXT1 image: one chunk of 2x2 blocks, all four taking the second colour endpoint.
    private static CrnBuilder Dxt1Image()
    {
        var level = new CrnBuilder.BitWriter();
        level.Symbol(false);            // chunk encoding 0
        level.Symbol(true);             // endpoint delta 1, so the chunk uses endpoint index 1
        for (int block = 0; block < 4; block++)
            level.Symbol(false);        // selector delta 0

        var crn = new CrnBuilder
        {
            Width = 8,
            Height = 8,
            Format = CrnBuilder.FormatDxt1,
            Tables = Tables(color: true, alpha: false),
            ColorEndpoints = (ColorEndpointStream(
                new[] { false, false, false, false, false, false },
                new[] { true, true, true, true, true, true }), 2),
            ColorSelectors = (SelectorStream(1, maxValue: 3), 1),
        };
        crn.Levels.Add(level.ToArray());
        return crn;
    }

    [Fact]
    public void Decode_Dxt1_MatchesTheBlocksTheStreamDescribes()
    {
        Assert.True(CrunchTexture.TryDecode(Dxt1Image().Build(), out byte[] blocks,
            out CrunchTexture.BlockFormat format, out int width, out int height, out int levels));

        Assert.Equal(CrunchTexture.BlockFormat.Dxt1, format);
        Assert.Equal(8, width);
        Assert.Equal(8, height);
        Assert.Equal(1, levels);

        // 2x2 blocks of 8 bytes: the endpoint pair built from deltas of 1, then all-zero selectors.
        Assert.Equal(32, blocks.Length);
        for (int block = 0; block < 4; block++)
        {
            Assert.Equal(EndpointOfOnes, BitConverter.ToUInt32(blocks, block * 8));
            Assert.Equal(0u, BitConverter.ToUInt32(blocks, (block * 8) + 4));
        }
    }

    [Fact]
    public void Decode_SecondMipLevel_IsAppendedAfterTheFirst()
    {
        // 4x4 is a single block whose chunk has its right column and bottom row clipped away: the stream
        // still carries a selector for each of the four block slots, but only one block is written.
        var level1 = new CrnBuilder.BitWriter();
        level1.Symbol(false);           // chunk encoding 0
        level1.Symbol(false);           // endpoint delta 0, so the chunk uses endpoint index 0
        for (int block = 0; block < 4; block++)
            level1.Symbol(false);

        CrnBuilder crn = Dxt1Image();
        crn.Levels.Add(level1.ToArray());

        Assert.True(CrunchTexture.TryDecode(crn.Build(), out byte[] blocks, out _, out _, out _, out int levels));
        Assert.Equal(2, levels);
        Assert.Equal(40, blocks.Length);

        // The clipped level is one block using the first endpoint, which every delta left at zero.
        Assert.Equal(0u, BitConverter.ToUInt32(blocks, 32));
        Assert.Equal(0u, BitConverter.ToUInt32(blocks, 36));
    }

    // A 4x4 DXT5 image: a single block, so both the right column and the bottom row of its chunk are
    // clipped away, while the stream still carries a selector for all four block slots.
    private static CrnBuilder Dxt5Image()
    {
        var level = new CrnBuilder.BitWriter();
        level.Symbol(false);            // chunk encoding 0
        level.Symbol(false);            // alpha endpoint delta 0
        level.Symbol(false);            // colour endpoint delta 0
        for (int block = 0; block < 4; block++)
        {
            level.Symbol(false);        // alpha selector delta
            level.Symbol(false);        // colour selector delta
        }

        var crn = new CrnBuilder
        {
            Width = 4,
            Height = 4,
            Format = CrnBuilder.FormatDxt5,
            Tables = Tables(color: true, alpha: true),
            ColorEndpoints = (ColorEndpointStream(new[] { true, true, true, true, true, true }), 1),
            ColorSelectors = (SelectorStream(1, maxValue: 3), 1),
            AlphaEndpoints = (AlphaEndpointStream(new[] { true, true }), 1),
            AlphaSelectors = (SelectorStream(1, maxValue: 7), 1),
        };
        crn.Levels.Add(level.ToArray());
        return crn;
    }

    [Fact]
    public void Decode_Dxt5_CarriesTheAlphaBlockAsWell()
    {
        Assert.True(CrunchTexture.TryDecode(Dxt5Image().Build(), out byte[] blocks,
            out CrunchTexture.BlockFormat format, out int width, out int height, out _));

        Assert.Equal(CrunchTexture.BlockFormat.Dxt5, format);
        Assert.Equal(4, width);
        Assert.Equal(4, height);
        Assert.Equal(16, blocks.Length);

        // The alpha half first (both endpoints stepped by one, selectors flat), then the colour half.
        Assert.Equal(0x0101u, BitConverter.ToUInt32(blocks, 0));
        Assert.Equal(0u, BitConverter.ToUInt32(blocks, 4));
        Assert.Equal(EndpointOfOnes, BitConverter.ToUInt32(blocks, 8));
        Assert.Equal(0u, BitConverter.ToUInt32(blocks, 12));
    }

    // An endpoint palette of `count` entries: the first is all zeroes and each later one steps every
    // channel by a further one, so entry k has all six channels equal to k.
    private static byte[] RisingEndpointStream(int count)
    {
        var entries = new bool[count][];
        for (int i = 0; i < count; i++)
            entries[i] = new[] { i > 0, i > 0, i > 0, i > 0, i > 0, i > 0 };
        return ColorEndpointStream(entries);
    }

    private static uint Endpoint(uint k) => k | (k << 5) | (k << 11) | (k << 16) | (k << 21) | (k << 27);

    [Fact]
    public void Decode_ChunksAreWalkedInSerpentineOrder()
    {
        // 16x16 is 4x4 blocks, so 2x2 chunks: the second chunk row runs right to left. Each chunk steps
        // the endpoint index by one, so which endpoint a block ends up with names the visit order.
        var level = new CrnBuilder.BitWriter();
        for (int chunk = 0; chunk < 4; chunk++)
        {
            // One decoded symbol carries three chunk encodings, so only the first and fourth read one.
            if (chunk is 0 or 3)
                level.Symbol(false);

            level.Symbol(true);         // endpoint delta 1
            for (int block = 0; block < 4; block++)
                level.Symbol(false);    // selector delta 0
        }

        var crn = new CrnBuilder
        {
            Width = 16,
            Height = 16,
            Format = CrnBuilder.FormatDxt1,
            Tables = Tables(color: true, alpha: false),
            ColorEndpoints = (RisingEndpointStream(4), 4),
            ColorSelectors = (SelectorStream(1, maxValue: 3), 1),
        };
        crn.Levels.Add(level.ToArray());

        Assert.True(CrunchTexture.TryDecode(crn.Build(), out byte[] blocks, out _, out _, out _, out _));
        Assert.Equal(128, blocks.Length);

        // Row pitch is 4 blocks * 8 bytes. Visit order: (0,0), (0,1), then back along (1,1), (1,0).
        Assert.Equal(Endpoint(1), BitConverter.ToUInt32(blocks, 0));   // chunk (0,0)
        Assert.Equal(Endpoint(2), BitConverter.ToUInt32(blocks, 16));  // chunk (0,1)
        Assert.Equal(Endpoint(3), BitConverter.ToUInt32(blocks, 80));  // chunk (1,1), reached first
        Assert.Equal(Endpoint(0), BitConverter.ToUInt32(blocks, 64));  // chunk (1,0), and the index wrapped
    }

    [Fact]
    public void Decode_FourTileChunk_GivesEachBlockItsOwnEndpoint()
    {
        // Chunk encoding 7 is the four-tile layout: every block in the chunk reads a different endpoint.
        var tables = new CrnBuilder.BitWriter();
        tables.Model(8, 0, 7); // chunk encodings: the second symbol is encoding 7
        tables.Model(2, 0, 1);
        tables.Model(2, 0, 1);

        var level = new CrnBuilder.BitWriter();
        level.Symbol(true);             // chunk encoding 7
        for (int tile = 0; tile < 4; tile++)
            level.Symbol(true);         // one endpoint delta per tile
        for (int block = 0; block < 4; block++)
            level.Symbol(false);

        var crn = new CrnBuilder
        {
            Width = 8,
            Height = 8,
            Format = CrnBuilder.FormatDxt1,
            Tables = tables.ToArray(),
            ColorEndpoints = (RisingEndpointStream(4), 4),
            ColorSelectors = (SelectorStream(1, maxValue: 3), 1),
        };
        crn.Levels.Add(level.ToArray());

        Assert.True(CrunchTexture.TryDecode(crn.Build(), out byte[] blocks, out _, out _, out _, out _));

        // Tiles 0..3 map to the blocks in reading order, and the fourth index wrapped back to zero.
        Assert.Equal(Endpoint(1), BitConverter.ToUInt32(blocks, 0));
        Assert.Equal(Endpoint(2), BitConverter.ToUInt32(blocks, 8));
        Assert.Equal(Endpoint(3), BitConverter.ToUInt32(blocks, 16));
        Assert.Equal(Endpoint(0), BitConverter.ToUInt32(blocks, 24));
    }

    [Fact]
    public void Decode_RejectsWhatIsNotACrunchContainerWeHandle()
    {
        // Too short to hold a header, and a header that does not start with the signature.
        Assert.False(CrunchTexture.TryDecode(Array.Empty<byte>(), out _, out _, out _, out _, out _));
        CrnBuilder wrongSignature = Dxt1Image();
        wrongSignature.SignatureOverride = 0x1234;
        Assert.False(CrunchTexture.TryDecode(wrongSignature.Build(), out _, out _, out _, out _, out _));

        // DXT5A, DXN and ETC1 are Crunch formats this decoder does not produce blocks for.
        CrnBuilder dxt5a = Dxt1Image();
        dxt5a.Format = 9;
        Assert.False(CrunchTexture.TryDecode(dxt5a.Build(), out _, out _, out _, out _, out _));

        // A cube map: six faces of blocks, which nothing in the game data uses.
        CrnBuilder cube = Dxt1Image();
        cube.Faces = 6;
        Assert.False(CrunchTexture.TryDecode(cube.Build(), out _, out _, out _, out _, out _));

        // A file with no mip levels at all: the header has nothing to point at.
        var noLevels = new CrnBuilder { Tables = Tables(color: true, alpha: false) };
        Assert.False(CrunchTexture.TryDecode(noLevels.Build(), out _, out _, out _, out _, out _));

        // A level count the header cannot carry offsets for, and one that is outright invalid.
        CrnBuilder tooManyLevels = Dxt1Image();
        tooManyLevels.LevelsOverride = 12;
        Assert.False(CrunchTexture.TryDecode(tooManyLevels.Build(), out _, out _, out _, out _, out _));
        CrnBuilder impossibleLevels = Dxt1Image();
        impossibleLevels.LevelsOverride = 17;
        Assert.False(CrunchTexture.TryDecode(impossibleLevels.Build(), out _, out _, out _, out _, out _));

        // A header that claims to be shorter than the fields it has to hold, or longer than the file.
        CrnBuilder shortHeader = Dxt1Image();
        shortHeader.HeaderSizeOverride = 40;
        Assert.False(CrunchTexture.TryDecode(shortHeader.Build(), out _, out _, out _, out _, out _));
        CrnBuilder longHeader = Dxt1Image();
        longHeader.HeaderSizeOverride = 4096;
        Assert.False(CrunchTexture.TryDecode(longHeader.Build(), out _, out _, out _, out _, out _));

        // Truncated after the header: the level bitstream is gone.
        byte[] full = Dxt1Image().Build();
        Assert.False(CrunchTexture.TryDecode(full[..70], out _, out _, out _, out _, out _));
    }

    [Fact]
    public void Decode_MissingPaletteModels_Fails()
    {
        // Tables that stop after the chunk encoding model: the colour delta models the palettes need are
        // simply not there, so the decode has to give up rather than read noise.
        var tables = new CrnBuilder.BitWriter();
        tables.Model(2, 0, 1);

        CrnBuilder crn = Dxt1Image();
        crn.Tables = tables.ToArray();
        Assert.False(CrunchTexture.TryDecode(crn.Build(), out _, out _, out _, out _, out _));
    }

    [Fact]
    public void Decode_ImageWithNoPalettes_Fails()
    {
        CrnBuilder crn = Dxt1Image();
        crn.ColorEndpoints = (Array.Empty<byte>(), 0);
        crn.ColorSelectors = (Array.Empty<byte>(), 0);
        Assert.False(CrunchTexture.TryDecode(crn.Build(), out _, out _, out _, out _, out _));

        // A DXT5 image needs the alpha palettes as well.
        CrnBuilder noAlpha = Dxt1Image();
        noAlpha.Format = CrnBuilder.FormatDxt5;
        Assert.False(CrunchTexture.TryDecode(noAlpha.Build(), out _, out _, out _, out _, out _));
    }

    // A DXT5 image of `blocks` x `blocks` blocks, every chunk taking the first palette entry.
    private static CrnBuilder Dxt5Grid(int blocks)
    {
        int chunks = (blocks + 1) / 2;
        var level = new CrnBuilder.BitWriter();
        for (int chunk = 0; chunk < chunks * chunks; chunk++)
        {
            if (chunk % 3 == 0)
                level.Symbol(false);    // one symbol carries three chunk encodings
            level.Symbol(false);        // alpha endpoint delta
            level.Symbol(false);        // colour endpoint delta
            for (int block = 0; block < 4; block++)
            {
                level.Symbol(false);    // alpha selector delta
                level.Symbol(false);    // colour selector delta
            }
        }

        CrnBuilder crn = Dxt5Image();
        crn.Width = crn.Height = blocks * 4;
        crn.Levels.Clear();
        crn.Levels.Add(level.ToArray());
        return crn;
    }

    [Fact]
    public void Decode_Dxt5_ClipsOddBlockCountsAndKeepsEvenOnesWhole()
    {
        // 3x3 blocks: two chunk rows, the last chunk of each row and column half outside the image.
        Assert.True(CrunchTexture.TryDecode(Dxt5Grid(3).Build(), out byte[] odd, out _, out _, out _, out _));
        Assert.Equal(3 * 3 * 16, odd.Length);
        for (int block = 0; block < 9; block++)
            Assert.Equal(0x0101u, BitConverter.ToUInt32(odd, block * 16)); // every block was written

        // 2x2 blocks: a single chunk that fits exactly, so nothing is clipped.
        Assert.True(CrunchTexture.TryDecode(Dxt5Grid(2).Build(), out byte[] even, out _, out _, out _, out _));
        Assert.Equal(2 * 2 * 16, even.Length);
        for (int block = 0; block < 4; block++)
            Assert.Equal(0x0101u, BitConverter.ToUInt32(even, block * 16));
    }

    [Fact]
    public void Decode_PaletteIndexThatOvershootsTwice_FallsBackToTheFirstEntry()
    {
        // Indices step by the decoded delta and wrap once by subtraction. A delta far larger than the
        // palette (only a damaged stream produces one) is left pointing nowhere, and reads entry zero.
        var tables = new CrnBuilder.BitWriter();
        tables.Model(2, 0, 1);      // chunk encodings
        tables.Model(50, 0, 49);    // colour endpoint delta: the second symbol is a delta of 49
        tables.Model(2, 0, 1);      // colour selector delta

        var level = new CrnBuilder.BitWriter();
        level.Symbol(false);        // chunk encoding 0
        level.Symbol(true);         // endpoint delta 49, against a palette of two
        for (int block = 0; block < 4; block++)
            level.Symbol(false);

        CrnBuilder crn = Dxt1Image();
        crn.Tables = tables.ToArray();
        crn.Levels.Clear();
        crn.Levels.Add(level.ToArray());

        Assert.True(CrunchTexture.TryDecode(crn.Build(), out byte[] blocks, out _, out _, out _, out _));
        Assert.Equal(0u, BitConverter.ToUInt32(blocks, 0)); // the first endpoint, which is all zeroes
    }

    [Fact]
    public void Decode_HeaderPointingOutsideTheFile_Fails()
    {
        // Tables that start past the end of the data.
        CrnBuilder badTables = Dxt1Image();
        badTables.TablesOffsetOverride = 0xFFFF;
        Assert.False(CrunchTexture.TryDecode(badTables.Build(), out _, out _, out _, out _, out _));

        // A level offset of zero (inside the header) and one past the end.
        CrnBuilder zeroLevel = Dxt1Image();
        zeroLevel.LevelOffsetsOverride = new[] { 0 };
        Assert.False(CrunchTexture.TryDecode(zeroLevel.Build(), out _, out _, out _, out _, out _));
        CrnBuilder farLevel = Dxt1Image();
        farLevel.LevelOffsetsOverride = new[] { 0xFFFF };
        Assert.False(CrunchTexture.TryDecode(farLevel.Build(), out _, out _, out _, out _, out _));

        // Two levels whose offsets run backwards, so the first level has no bytes to read.
        CrnBuilder backwards = Dxt1Image();
        backwards.Levels.Add(new byte[] { 0 });
        byte[] built = backwards.Build();
        backwards.LevelOffsetsOverride = new[] { built.Length - 1, 71 };
        Assert.False(CrunchTexture.TryDecode(backwards.Build(), out _, out _, out _, out _, out _));
    }

    [Fact]
    public void Decode_UnreadableTables_Fails()
    {
        // No tables at all, then tables that decode to no model.
        CrnBuilder empty = Dxt1Image();
        empty.Tables = Array.Empty<byte>();
        Assert.False(CrunchTexture.TryDecode(empty.Build(), out _, out _, out _, out _, out _));

        CrnBuilder noModel = Dxt1Image();
        noModel.Tables = new byte[] { 0, 0, 0, 0 };
        Assert.False(CrunchTexture.TryDecode(noModel.Build(), out _, out _, out _, out _, out _));

        // A DXT5 image whose tables stop before the alpha delta models.
        CrnBuilder dxt5 = Dxt5Image();
        dxt5.Tables = Tables(color: true, alpha: false);
        Assert.False(CrunchTexture.TryDecode(dxt5.Build(), out _, out _, out _, out _, out _));
    }

    [Fact]
    public void Decode_UnreadablePalette_Fails()
    {
        // Each palette in turn: first with no bytes to read, then with bytes that hold no model.
        foreach (bool empty in new[] { true, false })
        {
            byte[] broken = empty ? Array.Empty<byte>() : new byte[] { 0, 0, 0, 0 };

            CrnBuilder colorEndpoints = Dxt5Image();
            colorEndpoints.ColorEndpoints = (broken, 1);
            Assert.False(CrunchTexture.TryDecode(colorEndpoints.Build(), out _, out _, out _, out _, out _));

            CrnBuilder colorSelectors = Dxt5Image();
            colorSelectors.ColorSelectors = (broken, 1);
            Assert.False(CrunchTexture.TryDecode(colorSelectors.Build(), out _, out _, out _, out _, out _));

            CrnBuilder alphaEndpoints = Dxt5Image();
            alphaEndpoints.AlphaEndpoints = (broken, 1);
            Assert.False(CrunchTexture.TryDecode(alphaEndpoints.Build(), out _, out _, out _, out _, out _));

            CrnBuilder alphaSelectors = Dxt5Image();
            alphaSelectors.AlphaSelectors = (broken, 1);
            Assert.False(CrunchTexture.TryDecode(alphaSelectors.Build(), out _, out _, out _, out _, out _));
        }
    }

    [Fact]
    public void Decode_SelectorSymbolOutsideTheDeltaTable_Fails()
    {
        // The selector models index a fixed grid of delta pairs (49 for colour, 225 for alpha). A model
        // carrying a symbol beyond it can only come from a damaged stream.
        var colorSelectors = new CrnBuilder.BitWriter();
        colorSelectors.Model(60, 0, 59);
        for (int j = 0; j < 8; j++)
            colorSelectors.Symbol(true);

        CrnBuilder color = Dxt5Image();
        color.ColorSelectors = (colorSelectors.ToArray(), 1);
        Assert.False(CrunchTexture.TryDecode(color.Build(), out _, out _, out _, out _, out _));

        var alphaSelectors = new CrnBuilder.BitWriter();
        alphaSelectors.Model(240, 0, 239);
        for (int j = 0; j < 8; j++)
            alphaSelectors.Symbol(true);

        CrnBuilder alpha = Dxt5Image();
        alpha.AlphaSelectors = (alphaSelectors.ToArray(), 1);
        Assert.False(CrunchTexture.TryDecode(alpha.Build(), out _, out _, out _, out _, out _));
    }
}
