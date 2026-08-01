using System;
using System.Collections.Generic;
using System.Linq;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

// The entropy layer on its own: the canonical-code tables, the bit reader and the run-length encoding a
// model's code sizes are sent with. CrunchTextureTests drives the same code through whole images; this
// covers the shapes real textures happen not to use.
public class CrunchCodecTests
{
    // A code-length alphabet wide enough to send literal sizes and every run escape. Eight symbols of
    // three bits each, so the canonical code of a value is its position in this list.
    private static readonly int[] CodelengthAlphabet = { 0, 1, 2, 3, 17, 18, 19, 20 };

    private static void ModelHeader(CrnBuilder.BitWriter w, int totalSymbols)
    {
        w.Bits((uint)totalSymbols, 14);
        w.Bits((uint)CrnBuilder.MostProbableCodelengthCodes.Length, 5);
        foreach (int code in CrnBuilder.MostProbableCodelengthCodes)
            w.Bits(CodelengthAlphabet.Contains(code) ? 3u : 0u, 3);
    }

    // Emits one code-length symbol: a literal size, or one of the run escapes (17..20).
    private static void Codelength(CrnBuilder.BitWriter w, int value) =>
        w.Bits((uint)Array.IndexOf(CodelengthAlphabet, value), 3);

    private static CrunchCodec Codec(byte[] data)
    {
        var codec = new CrunchCodec(data);
        Assert.True(codec.StartDecoding(0, data.Length));
        return codec;
    }

    [Fact]
    public void StartDecoding_RejectsARangeOutsideTheBuffer()
    {
        var codec = new CrunchCodec(new byte[4]);
        Assert.False(codec.StartDecoding(0, 0));
        Assert.False(codec.StartDecoding(-1, 2));
        Assert.False(codec.StartDecoding(2, 4));
        Assert.True(codec.StartDecoding(2, 2));
    }

    [Fact]
    public void GetBits_ReadsWideValuesAndRunsOutIntoZeroes()
    {
        CrunchCodec codec = Codec(new byte[] { 0xAB, 0xCD, 0xEF, 0x12 });

        Assert.Equal(0u, codec.GetBits(0));
        Assert.Equal(0xABCDEFu, codec.GetBits(24)); // more than 16 bits, so read in two halves
        Assert.Equal(0x12u, codec.GetBits(8));
        Assert.Equal(0u, codec.GetBits(16));        // past the end: the reader keeps feeding zeroes
    }

    [Fact]
    public void Build_RejectsCodeSizesItCannotTurnIntoATable()
    {
        Assert.Null(CrunchHuffmanTable.Build(Array.Empty<byte>()));
        Assert.Null(CrunchHuffmanTable.Build(new byte[] { 17 })); // longer than a code can be
        Assert.Null(CrunchHuffmanTable.Build(new byte[] { 0, 0 })); // no symbol is used at all
    }

    [Fact]
    public void Decode_UsesTheLookupTableForShortCodesAndTheSearchForLongOnes()
    {
        // Twenty symbols puts the table in play (it covers codes up to six bits); the two seven-bit
        // codes fall through to the linear search instead.
        var sizes = new byte[20];
        for (int i = 0; i < 7; i++)
            sizes[i] = (byte)(i + 1);
        sizes[7] = 7;

        CrunchHuffmanTable? table = CrunchHuffmanTable.Build(sizes);
        Assert.NotNull(table);
        Assert.Equal(20, table!.TotalSymbols);

        // Canonical codes: 0, 10, 110, ... 1111110, 1111111.
        var stream = new CrnBuilder.BitWriter();
        for (int symbol = 0; symbol < 7; symbol++)
            stream.Bits((uint)((1 << (symbol + 1)) - 2), symbol + 1);
        stream.Bits(0b1111111, 7);

        CrunchCodec codec = Codec(stream.ToArray());
        for (int symbol = 0; symbol < 8; symbol++)
            Assert.Equal(symbol, codec.Decode(table));
    }

    [Fact]
    public void Decode_TableWideEnoughForEveryCode_NeverFallsBackToTheSearch()
    {
        // Twenty symbols with no code longer than five bits: the six-bit lookup answers everything, so
        // the linear search has no length to start at beyond the table.
        var sizes = new byte[20];
        foreach (int i in new[] { 0, 1, 2, 3, 4, 5 })
            sizes[i] = (byte)Math.Min(i + 1, 5);

        CrunchHuffmanTable? table = CrunchHuffmanTable.Build(sizes);
        Assert.NotNull(table);

        var stream = new CrnBuilder.BitWriter();
        for (int symbol = 0; symbol < 5; symbol++)
            stream.Bits((uint)((1 << (symbol + 1)) - 2), symbol + 1);
        stream.Bits(0b11111, 5);

        CrunchCodec codec = Codec(stream.ToArray());
        for (int symbol = 0; symbol < 6; symbol++)
            Assert.Equal(symbol, codec.Decode(table!));
    }

    [Fact]
    public void Decode_SkipsLengthsWithNoCodesWhenLeavingTheTable()
    {
        // Codes of one to six bits, then four of eight: nothing is seven bits long, so the search has to
        // step over that length to find where the codes resume.
        var sizes = new byte[20];
        for (int i = 0; i < 6; i++)
            sizes[i] = (byte)(i + 1);
        for (int i = 6; i < 10; i++)
            sizes[i] = 8;

        CrunchHuffmanTable? table = CrunchHuffmanTable.Build(sizes);
        Assert.NotNull(table);

        // The eight-bit codes start right after the six-bit one: 11111100 upwards.
        var stream = new CrnBuilder.BitWriter();
        stream.Bits(0b11111100, 8);
        stream.Bits(0b11111111, 8);

        CrunchCodec codec = Codec(stream.ToArray());
        Assert.Equal(6, codec.Decode(table!));
        Assert.Equal(9, codec.Decode(table!));
    }

    [Fact]
    public void Decode_CodeTheTableDoesNotHold_YieldsSymbolZeroAndConsumesNothing()
    {
        // A model with a single one-bit code says nothing about a stream that starts with a 1. The
        // reference decoder answers symbol 0 without moving the read head, and the whole format leans on
        // that: a palette with one entry is encoded by writing no bits at all.
        CrunchHuffmanTable? table = CrunchHuffmanTable.Build(new byte[] { 1 });
        Assert.NotNull(table);

        CrunchCodec codec = Codec(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        for (int i = 0; i < 4; i++)
            Assert.Equal(0, codec.Decode(table!));

        // The head really did not move: the bits are all still there.
        Assert.Equal(0xFFFFu, codec.GetBits(16));
    }

    [Fact]
    public void ReceiveModel_ExpandsTheRunLengthEscapes()
    {
        // Sizes for twenty symbols: one literal, a small repeat of it, a large zero run, a small zero
        // run, then two literal zeroes. The four two-bit codes that come out form a complete code.
        var w = new CrnBuilder.BitWriter();
        ModelHeader(w, 20);
        Codelength(w, 2);           // symbol 0 has a two-bit code
        Codelength(w, 19); w.Bits(0, 2);   // small repeat: three more symbols like it
        Codelength(w, 18); w.Bits(0, 7);   // large zero run: eleven symbols
        Codelength(w, 17); w.Bits(0, 3);   // small zero run: three symbols
        Codelength(w, 0);
        Codelength(w, 0);

        // Four symbols of two bits each: 00, 01, 10, 11.
        for (int symbol = 0; symbol < 4; symbol++)
            w.Bits((uint)symbol, 2);

        CrunchCodec codec = Codec(w.ToArray());
        CrunchHuffmanTable? table = codec.ReceiveModel();
        Assert.NotNull(table);

        for (int symbol = 0; symbol < 4; symbol++)
            Assert.Equal(symbol, codec.Decode(table!));
    }

    [Fact]
    public void ReceiveModel_ExpandsALargeRepeat()
    {
        var w = new CrnBuilder.BitWriter();
        ModelHeader(w, 20);
        Codelength(w, 3);
        Codelength(w, 20); w.Bits(0, 6);   // large repeat: seven more symbols with that size
        for (int i = 0; i < 12; i++)
            Codelength(w, 0);

        CrunchCodec codec = Codec(w.ToArray());
        CrunchHuffmanTable? table = codec.ReceiveModel();
        Assert.NotNull(table);
        Assert.Equal(20, table!.TotalSymbols);
    }

    [Fact]
    public void Build_RejectsSizesThatOverfillTheCodeSpace()
    {
        // Three one-bit codes cannot exist: only two fit. Real encoders never emit this, but a damaged
        // stream can decode into it, and the tables must not be built from it.
        Assert.Null(CrunchHuffmanTable.Build(new byte[] { 1, 1, 1 }));
        Assert.Null(CrunchHuffmanTable.Build(new byte[] { 1, 2, 2, 2 }));
    }

    [Fact]
    public void ReceiveModel_RejectsAMalformedDescription()
    {
        Assert.Null(Codec(new byte[] { 0, 0, 0, 0 }).ReceiveModel());              // no symbols at all
        Assert.Null(Codec(Header(totalSymbols: 8, codelengthCodes: 0)).ReceiveModel());
        Assert.Null(Codec(Header(totalSymbols: 8, codelengthCodes: 22)).ReceiveModel());

        // A code-length alphabet where every size is zero: there is no table to decode the sizes with.
        var empty = new CrnBuilder.BitWriter();
        empty.Bits(8, 14);
        empty.Bits((uint)CrnBuilder.MostProbableCodelengthCodes.Length, 5);
        foreach (int _ in CrnBuilder.MostProbableCodelengthCodes)
            empty.Bits(0, 3);
        Assert.Null(Codec(empty.ToArray()).ReceiveModel());

        // Runs that overshoot the symbol count, in both the zero and the repeat flavour.
        Assert.Null(Run(w => { Codelength(w, 17); w.Bits(7, 3); }));    // ten zeroes into four symbols
        Assert.Null(Run(w => { Codelength(w, 18); w.Bits(0, 7); }));    // eleven zeroes into four
        Assert.Null(Run(w => { Codelength(w, 3); Codelength(w, 20); w.Bits(0, 6); }));

        // A repeat with nothing to repeat: at the very start, and right after a zero size.
        Assert.Null(Run(w => { Codelength(w, 19); w.Bits(0, 2); }));
        Assert.Null(Run(w => { Codelength(w, 0); Codelength(w, 19); w.Bits(0, 2); }));

        // Sizes that describe no usable code: four symbols, none of them coded.
        Assert.Null(Run(w =>
        {
            for (int i = 0; i < 4; i++)
                Codelength(w, 0);
        }));
    }

    // A model header alone, for the cases that fail before any code size is read.
    private static byte[] Header(int totalSymbols, int codelengthCodes)
    {
        var w = new CrnBuilder.BitWriter();
        w.Bits((uint)totalSymbols, 14);
        w.Bits((uint)codelengthCodes, 5);
        return w.ToArray();
    }

    // Reads a four-symbol model whose code sizes are written by `body`.
    private static CrunchHuffmanTable? Run(Action<CrnBuilder.BitWriter> body)
    {
        var w = new CrnBuilder.BitWriter();
        ModelHeader(w, 4);
        body(w);
        return Codec(w.ToArray()).ReceiveModel();
    }
}
