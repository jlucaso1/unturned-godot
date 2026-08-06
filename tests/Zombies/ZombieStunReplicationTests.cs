using System;
using System.Collections.Generic;
using UnturnedGodot.Net;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Zombies;

// ZombieManager.sendZombieStun on the wire. Reliable, like a kill: a dropped stun leaves a body frozen on
// the server and walking on every client, and nothing later says otherwise.
public class ZombieStunReplicationTests
{
    [Fact]
    public void RoundTripsABoundAndItsStuns()
    {
        byte[] payload = ZombieNetMessages.WriteZombieStunned(3,
            new (ushort, byte)[] { (1, 0), (65535, 10), (900, 4) });

        Assert.Equal((byte)ENetMessage.ZombieStunned, payload[0]);

        (byte bound, List<(ushort Id, byte Clip)> stuns) = ZombieNetMessages.ReadZombieStunned(payload);

        Assert.Equal(3, bound);
        Assert.Equal(new (ushort, byte)[] { (1, 0), (65535, 10), (900, 4) }, stuns);
    }

    [Fact]
    public void RoundTripsAnEmptyList() =>
        Assert.Empty(ZombieNetMessages.ReadZombieStunned(
            ZombieNetMessages.WriteZombieStunned(0, Array.Empty<(ushort, byte)>())).Stuns);

    // The count header is a byte. A silent truncation would be the worst outcome — a payload advertising
    // fewer zombies than it carries, leaving the rest walking through their stagger.
    [Fact]
    public void MoreThanAByteOfZombiesIsRefusedRatherThanTruncated()
    {
        var many = new (ushort, byte)[256];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ZombieNetMessages.WriteZombieStunned(0, many));
    }

    [Fact]
    public void WriteRejectsANullList() =>
        Assert.Throws<ArgumentNullException>(() => ZombieNetMessages.WriteZombieStunned(0, null!));

    // Little-endian ids each followed by their reel, behind a type/bound/count header — the same shape
    // every other zombie payload uses. Pinned byte for byte, because a layout change that both ends
    // agree on still breaks every client running the older build, and the protocol version is what has
    // to move with it.
    [Fact]
    public void WireLayoutIsExact()
    {
        byte[] payload = ZombieNetMessages.WriteZombieStunned(7,
            new (ushort, byte)[] { (0x0201, 4), (0x0403, 9) });

        Assert.Equal(new byte[]
        {
            (byte)ENetMessage.ZombieStunned, 7, 2,
            0x01, 0x02, 4,
            0x03, 0x04, 9,
        }, payload);
    }

    // Every truncation has to read as a bad datagram rather than as an escaping exception: this decodes
    // inside the client's receive loop.
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void ATruncatedPayloadIsADecodeFailure(int keep)
    {
        byte[] truncated = ZombieNetMessages.WriteZombieStunned(0,
            new (ushort, byte)[] { (1, 2), (3, 4) })[..keep];

        Assert.False(MalformedPacket.TryDecode(truncated, ZombieNetMessages.ReadZombieStunned, out _));
    }
}
