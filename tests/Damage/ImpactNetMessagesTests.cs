using System;
using System.IO;
using System.Text;
using Godot;
using UnturnedGodot.Damage;
using UnturnedGodot.Net;
using Xunit;

namespace UnturnedGodot.Tests.Damage;

// ReceiveSpawnLegacyImpact on the wire. Every byte here comes off a socket, so the reader has to survive
// a datagram that lies about itself as readily as it decodes an honest one.
public class ImpactNetMessagesTests
{
    private static readonly PunchImpact Sample = new(
        3, new Vector3(12.5f, -3.25f, 640f), new Vector3(0f, 1f, 0f), "Wood_Static");

    [Fact]
    public void RoundTripsAnImpact()
    {
        PunchImpact read = ImpactNetMessages.ReadImpact(ImpactNetMessages.WriteImpact(Sample));

        Assert.Equal(Sample, read);
    }

    [Fact]
    public void TheFirstByteNamesTheMessage() =>
        Assert.Equal((byte)ENetMessage.Impact, ImpactNetMessages.WriteImpact(Sample)[0]);

    // A hit on a zombie carries "Flesh" and one on a wall a longer name; neither is special, but a length
    // prefix that disagreed with the payload would only show up on one of them.
    [Theory]
    [InlineData("Flesh")]
    [InlineData("Peaks_Grass_Dry")]
    [InlineData("A")]
    public void RoundTripsSurfaceNamesOfAnyLength(string surface)
    {
        var impact = Sample with { Surface = surface };

        Assert.Equal(surface, ImpactNetMessages.ReadImpact(ImpactNetMessages.WriteImpact(impact)).Surface);
    }

    // The game's own ceiling is 63 characters (NAME_LENGTH_BITS = 6). A longer one is truncated rather
    // than refused: the payload still decodes, and a surface nothing can resolve simply makes no sound.
    [Fact]
    public void ASurfaceNameLongerThanTheWireAllowsIsTruncatedRatherThanCorrupting()
    {
        var impact = Sample with { Surface = new string('x', 200) };

        PunchImpact read = ImpactNetMessages.ReadImpact(ImpactNetMessages.WriteImpact(impact));

        Assert.Equal(ImpactNetMessages.MaxSurfaceBytes, read.Surface.Length);
        Assert.Equal(impact.Point, read.Point);
    }

    // Nothing raises an impact with no surface — the host gates on HasSurface — but the encoding must not
    // depend on that, or a future caller's empty name becomes a malformed datagram.
    [Fact]
    public void RoundTripsAnEmptySurface() =>
        Assert.Equal(string.Empty,
            ImpactNetMessages.ReadImpact(ImpactNetMessages.WriteImpact(Sample with { Surface = "" })).Surface);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(13)]
    [InlineData(26)]
    public void ATruncatedHeaderIsADecodeFailureRatherThanACrash(int keep)
    {
        byte[] truncated = ImpactNetMessages.WriteImpact(Sample)[..keep];

        Exception? e = Record.Exception(() => ImpactNetMessages.ReadImpact(truncated));

        Assert.NotNull(e);
        Assert.True(MalformedPacket.IsDecodeFailure(e!),
            $"a truncated impact should read as a bad datagram, got {e!.GetType().Name}");
    }

    // The nastiest shape: a header that survives, declaring more surface than the datagram carries. A
    // reader that trusted the length would run off the end of the array.
    [Fact]
    public void ASurfaceLengthPastTheEndOfThePayloadIsADecodeFailure()
    {
        byte[] payload = ImpactNetMessages.WriteImpact(Sample);
        payload[26] = 60; // the name is 11 bytes long

        Assert.Throws<EndOfStreamException>(() => ImpactNetMessages.ReadImpact(payload));
    }

    // Bytes that are not UTF-8 reach the decoder as readily as bytes that are. Whatever comes out, it must
    // come out as a return or as a decode failure — never as an escaping exception in the receive loop.
    [Fact]
    public void NonUtf8SurfaceBytesDoNotEscapeTheDecoder()
    {
        byte[] payload = ImpactNetMessages.WriteImpact(Sample with { Surface = "ab" });
        payload[^1] = 0xFF;
        payload[^2] = 0xC0;

        Exception? e = Record.Exception(() => ImpactNetMessages.ReadImpact(payload));

        Assert.True(e == null || MalformedPacket.IsDecodeFailure(e),
            $"bad UTF-8 should decode or fail cleanly, got {e?.GetType().Name}");
    }

    [Fact]
    public void ReadingNothingIsRefused() =>
        Assert.Throws<ArgumentNullException>(() => ImpactNetMessages.ReadImpact(null!));

    // The whole guard the receive loop actually relies on: MalformedPacket has to classify every failure
    // above as a bad datagram, or one hostile packet takes the client down inside _PhysicsProcess.
    [Fact]
    public void EveryMalformedShapeIsCaughtByTheReceiveLoopsFilter()
    {
        byte[] whole = ImpactNetMessages.WriteImpact(Sample);
        var bad = new byte[][]
        {
            whole[..4],
            whole[..26],
            Corrupt(whole, 26, 200),
            Encoding.UTF8.GetBytes("nonsense"),
        };

        foreach (byte[] payload in bad)
        {
            Assert.False(
                MalformedPacket.TryDecode(payload, ImpactNetMessages.ReadImpact, out _),
                $"a {payload.Length}-byte payload decoded when it should not have");
        }
    }

    private static byte[] Corrupt(byte[] source, int index, byte value)
    {
        byte[] copy = (byte[])source.Clone();
        copy[index] = value;
        return copy;
    }
}
