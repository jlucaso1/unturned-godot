using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Net;
using UnturnedGodot.Player;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Net;

// The MTU invariant, asserted over every writer that walks a collection, at the largest input the rest
// of the system can legally hand it.
//
// This is the test the codebase did not have. ZombieList was chunked and said why — "reliable datagrams
// are not fragmented" — and every sibling payload was written as though the transport's 16 KiB ceiling
// were a budget. It is not: it bounds what the transport will ACCEPT, while the path bounds what
// survives in one piece. A datagram past the path MTU becomes two to four IP fragments that must all
// arrive, so a stream's effective loss rate compounds with its fragment count, worst exactly when the
// server is busiest.
//
// It did not bite because of the defaults. NavBound.MaxZombies is 64 out of the box, and 6 + 16*64 is
// 1030 bytes — under the line by luck. The map file may set 255 (it is a byte), the transport admits
// 254 players, and neither number is guarded anywhere. So the assertions below are written against the
// LEGAL maxima rather than the observed ones.
public class DatagramBudgetTests
{
    // Everything a writer can be handed, at its ceiling: 255 zombies in a region (NavBound.MaxZombies is
    // a byte) and 254 players on a server (PlayerIdPool's ceiling).
    private const int MaxZombiesInRegion = byte.MaxValue;
    private const int MaxPlayers = 254;

    [Fact]
    public void EveryChunkedWriterStaysInsideTheBudgetAtItsLegalMaximum()
    {
        foreach (byte[] chunk in NetMessages.WriteStateUpdates(9, Snapshots(MaxPlayers)))
            AssertFits(chunk, "StateUpdate");

        foreach (byte[] chunk in ZombieNetMessages.WriteZombieStateChunks(9, ZombieStates(MaxZombiesInRegion)))
            AssertFits(chunk, "ZombieStates");

        foreach (byte[] chunk in ZombieNetMessages.WriteZombieLists(3, Listings(MaxZombiesInRegion)))
            AssertFits(chunk, "ZombieList");

        foreach (byte[] chunk in ZombieNetMessages.WriteZombieKilledChunks(3, Kills(MaxZombiesInRegion)))
            AssertFits(chunk, "ZombieKilled");

        foreach (byte[] chunk in ZombieNetMessages.WriteZombieStunnedChunks(3, Stuns(MaxZombiesInRegion)))
            AssertFits(chunk, "ZombieStunned");

        foreach (byte[] chunk in NetMessages.WriteWelcomeChunks(1, 9, 4, Roster(MaxPlayers)))
            AssertFits(chunk, "Welcome");
    }

    // The other half of the guarantee: a single-datagram writer handed more than fits REFUSES rather
    // than quietly producing a fragmented payload. Without this the split above would be a convention
    // that any future caller could bypass by calling the singular form, which is exactly how the
    // discipline came to apply to ZombieList and nothing else.
    [Fact]
    public void ASingleDatagramWriterRefusesMoreThanItCanCarry()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NetMessages.WriteStateUpdate(0, Snapshots(NetMessages.MaxSnapshotsPerDatagram + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ZombieNetMessages.WriteZombieStates(0, ZombieStates(ZombieNetMessages.MaxStatesPerDatagram + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ZombieNetMessages.WriteZombieList(0, Listings(ZombieNetMessages.ListChunkSize + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ZombieNetMessages.WriteZombieKilled(0, Kills(ZombieNetMessages.MaxKilledPerDatagram + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ZombieNetMessages.WriteZombieStunned(0, Stuns(ZombieNetMessages.MaxStunnedPerDatagram + 1)));
    }

    // And at the ceiling exactly, it writes — a bound that refused its own legal maximum would push
    // every caller into the chunked form for payloads that never needed splitting.
    [Fact]
    public void ASingleDatagramWriterAcceptsExactlyItsCeiling()
    {
        AssertFits(NetMessages.WriteStateUpdate(0, Snapshots(NetMessages.MaxSnapshotsPerDatagram)),
            "StateUpdate");
        AssertFits(
            ZombieNetMessages.WriteZombieStates(0, ZombieStates(ZombieNetMessages.MaxStatesPerDatagram)),
            "ZombieStates");
        AssertFits(ZombieNetMessages.WriteZombieList(0, Listings(ZombieNetMessages.ListChunkSize)),
            "ZombieList");
    }

    // Every per-datagram ceiling also has to fit the one-byte count header each of these payloads uses.
    // Three bytes a stagger fits 399 inside the MTU budget, and the 400th would wrap the header to 144 —
    // a payload claiming to carry 144 zombies while holding 400, which the reader would believe.
    [Fact]
    public void NoCeilingExceedsWhatTheOneByteCountHeaderCanExpress()
    {
        Assert.True(NetMessages.MaxSnapshotsPerDatagram <= byte.MaxValue);
        Assert.True(ZombieNetMessages.MaxStatesPerDatagram <= byte.MaxValue);
        Assert.True(ZombieNetMessages.ListChunkSize <= byte.MaxValue);
        Assert.True(ZombieNetMessages.MaxKilledPerDatagram <= byte.MaxValue);
        Assert.True(ZombieNetMessages.MaxStunnedPerDatagram <= byte.MaxValue);
    }

    // Splitting must not lose or reorder a record: a chunked stream is only equivalent to the big
    // payload it replaced if reading every chunk in order rebuilds exactly what went in.
    [Fact]
    public void SplittingPreservesEveryRecordInOrder()
    {
        List<PlayerSnapshotState> states = Snapshots(MaxPlayers);
        var seen = new List<byte>();
        List<byte[]> chunks = NetMessages.WriteStateUpdates(77, states);

        Assert.True(chunks.Count > 1, "254 players is more than one datagram; the test proves nothing "
            + "if it never split");
        foreach (byte[] chunk in chunks)
        {
            (uint tick, List<PlayerSnapshotState> read) = NetMessages.ReadStateUpdate(chunk);
            Assert.Equal(77u, tick); // every chunk carries the same tick: they are one frame, not four
            foreach (PlayerSnapshotState s in read)
                seen.Add(s.PlayerId);
        }

        Assert.Equal(states.Count, seen.Count);
        for (int i = 0; i < states.Count; i++)
            Assert.Equal(states[i].PlayerId, seen[i]);
    }

    // An empty collection still produces one payload. A Welcome with nobody on the server has to tell
    // the joining player their own id, and a split that returned nothing would have swallowed it.
    [Fact]
    public void AnEmptyCollectionStillProducesExactlyOnePayload()
    {
        Assert.Single(NetChunks.Split(Array.Empty<int>(), 10, chunk => new byte[] { (byte)chunk.Count }));
        Assert.Single(NetMessages.WriteWelcomeChunks(1, 0, 0, Array.Empty<PlayerListing>()));
    }

    [Fact]
    public void CapacityIsAtLeastOneRecordAndRejectsANonsenseSize()
    {
        // A record bigger than the whole budget cannot be split by this mechanism, and emitting zero of
        // them would be a payload that says "nothing here" about a collection that is not empty.
        Assert.Equal(1, NetChunks.Capacity(headerBytes: 0, itemBytes: NetChunks.MaxPayloadBytes * 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetChunks.Capacity(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NetChunks.Split(new[] { 1 }, 0, _ => Array.Empty<byte>()));
        Assert.Throws<ArgumentNullException>(() =>
            NetChunks.Split<int>(null!, 1, _ => Array.Empty<byte>()));
        Assert.Throws<ArgumentNullException>(() => NetChunks.Split(new[] { 1 }, 1, null!));
    }

    // The window handed to a writer is a view onto the original list rather than a copy — a per-tick
    // payload builder must not allocate a List per chunk per connection. It still has to behave like a
    // list, including refusing an index past its own end rather than reading its neighbour's records.
    [Fact]
    public void TheChunkWindowBehavesLikeTheListItViews()
    {
        var source = new[] { 10, 20, 30, 40, 50 };
        var windows = new List<IReadOnlyList<int>>();
        NetChunks.Split(source, 2, chunk =>
        {
            windows.Add(chunk);
            var copied = new List<int>();
            foreach (int value in chunk) // the enumerator, not just the indexer
                copied.Add(value);
            Assert.Equal(copied.Count, chunk.Count);
            Assert.Throws<ArgumentOutOfRangeException>(() => chunk[chunk.Count]);
            Assert.Throws<ArgumentOutOfRangeException>(() => chunk[-1]);
            return Array.Empty<byte>();
        });

        Assert.Equal(3, windows.Count);
        Assert.Equal(new[] { 10, 20 }, windows[0]);
        Assert.Equal(new[] { 30, 40 }, windows[1]);
        Assert.Equal(new[] { 50 }, windows[2]);
    }

    private static void AssertFits(byte[] payload, string what) =>
        Assert.True(payload.Length <= NetChunks.MaxPayloadBytes,
            $"{what} wrote {payload.Length} bytes, past the {NetChunks.MaxPayloadBytes}-byte budget: "
            + "that datagram is IP-fragmented, and losing any one fragment loses all of it");

    private static List<PlayerSnapshotState> Snapshots(int count)
    {
        var states = new List<PlayerSnapshotState>(count);
        for (int i = 0; i < count; i++)
            states.Add(new PlayerSnapshotState((byte)i, new Vector3(i, i, i), 90, 0,
                EPlayerStance.Stand, moving: true, grounded: true));
        return states;
    }

    private static List<PlayerListing> Roster(int count)
    {
        var roster = new List<PlayerListing>(count);
        for (int i = 0; i < count; i++)
            roster.Add(new PlayerListing
            {
                PlayerId = (byte)i,
                // The longest name the protocol allows, so the roster is measured at ITS maximum too.
                Name = new string('W', NetMessages.MaxNameBytes),
                Position = new Vector3(i, i, i),
                Stance = EPlayerStance.Stand,
            });
        return roster;
    }

    private static List<ZombieSnapshotState> ZombieStates(int count)
    {
        var states = new List<ZombieSnapshotState>(count);
        for (int i = 0; i < count; i++)
            states.Add(new ZombieSnapshotState { Id = (ushort)i, Position = new Vector3(i, 0, i) });
        return states;
    }

    private static List<ZombieListing> Listings(int count)
    {
        var listings = new List<ZombieListing>(count);
        for (int i = 0; i < count; i++)
            listings.Add(new ZombieListing { Id = (ushort)i, Position = new Vector3(i, 0, i) });
        return listings;
    }

    private static List<(ushort, Vector3)> Kills(int count)
    {
        var kills = new List<(ushort, Vector3)>(count);
        for (int i = 0; i < count; i++)
            kills.Add(((ushort)i, Vector3.One));
        return kills;
    }

    private static List<(ushort, byte)> Stuns(int count)
    {
        var stuns = new List<(ushort, byte)>(count);
        for (int i = 0; i < count; i++)
            stuns.Add(((ushort)i, 1));
        return stuns;
    }
}
