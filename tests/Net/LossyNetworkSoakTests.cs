using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Net;
using UnturnedGodot.Player;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Net;

// The one test that runs the whole session over a wire that behaves like a wire.
//
// Five hand-rolled compensations exist in this codebase for the fact that reliable delivery
// retransmits but does not ORDER — the roster version, the gesture tick floor, the player tombstone
// table, the zombie death tombstones and the id recycling quarantine. Every one has its own tests,
// and every one of those tests builds ONE reordering by hand: deliver B then A, assert A loses.
//
// What none of them can produce is the state where the guards have to hold at the same time — a
// PlayerLeft overtaking a PlayerJoined for a recycled id while a ZombieKilled overtakes its region's
// ZombieList while a gesture lands for a player whose roster entry is still in flight. That is the
// state a hobby netcode gets wrong, and until LossyLoopbackTransport there was no way to reach it.
//
// Deterministic and sub-second, so it belongs in the hermetic CI matrix rather than in a nightly.
// It is seeded: a failure reproduces exactly from the seed printed in its message.
//
// ---------------------------------------------------------------------------------------------------
// What this actually catches, measured rather than hoped
//
// A soak that cannot fail is worse than no soak, so each guard was broken in turn and the run repeated:
//
//   BROKEN                                                     RESULT
//   RemotePlayer.PushGesture's dedup (<= 0 becomes < 0)        CAUGHT — "Stable1 played 154 of
//                                                              Stable0's 146 accepted swings"
//   NetClient's PlayerLeft tombstone (stops recording)         CAUGHT — 51 divergences, the first at
//                                                              tick 200: ghost avatars that never leave
//   NetClient's stale-PlayerLeft version guard                 NOT caught
//   NetClient's stale-PlayerJoined version guard               NOT caught
//   ...both of the above, with PlayerIdPool's quarantine       NOT caught
//   also set to zero
//
// The two that survive are worth being precise about, because "the test does not reach it" is a claim
// about the CODE here, not an excuse. Both guards defend the same race — a stale PlayerLeft landing
// after the PlayerJoined for whoever inherited the id — and that race is closed twice over upstream:
// PlayerIdPool quarantines a returned id for ReliableChannel.GiveUpAfter + 5 s (~187 ticks), and it
// recycles in RELEASE ORDER, so the id handed out is the one that has been free longest rather than
// the one just freed. Even with the quarantine set to zero, FIFO alone means a recycled id's old
// PlayerLeft was delivered hundreds of ticks earlier. The version guards are defence in depth beneath
// two structural defences, and no transport-level reordering can reach them while both hold. Reaching
// them needs a test that drives NetClient's message handlers directly — which is what
// tests/Net/NetClientRosterTests.cs already does.
public class LossyNetworkSoakTests
{
    private const string Level = "PEI";
    private static readonly Vector3 Spawn = new(0, 10f, 0);

    private static bool FlatGround(float x, float z, out float y)
    {
        y = 10f;
        return true;
    }

    // ---- what the soak is configured to do ---------------------------------------------------------

    private const int Ticks = 2000;
    private const int ClientCount = 8;

    // The first four never leave, so every swing thrown between them is owed to every one of them. The
    // last four churn — leaving and rejoining — which is what drives id recycling and therefore the
    // quarantine, the tombstones and the gesture floor.
    private const int StableClients = 4;

    private const double LossRate = 0.10;
    private const double DuplicateRate = 0.05;
    private const int ReorderWindow = 3;

    // A reliable datagram that hit a loss roll is pushed back by at most ReorderWindow + 1 + jitter
    // slots, and a duplicate by at most ReorderWindow. Pumping this many quiet rounds therefore drains
    // everything already sent, which is what makes "the rosters agree" a claim about a SETTLED session
    // rather than a snapshot taken mid-delivery.
    private const int DrainRounds = 24;

    // How often the run stops, drains and compares the world to the server's.
    private const int CheckpointEvery = 200;

    private sealed class Soak
    {
        public readonly LossPolicy Policy;
        public readonly LossyLoopbackServerTransport ServerTransport;
        public readonly NetServer Server;
        public readonly ZombieSystem Zombies;
        public readonly ZombieHost ZombieHost;
        public readonly List<Peer> Peers = new();
        public double Now = 5000.0;

        // Server truth, accumulated as it happens.
        public readonly HashSet<ushort> KilledZombies = new();

        // Every swing the server ACCEPTED, as (thrower name, server tick). The name rather than the id,
        // because ids are recycled and the question "did this swing play on the right avatar" is about
        // the person.
        public readonly List<(string Thrower, uint Tick)> AcceptedSwings = new();

        public Soak(ulong seed)
        {
            Policy = new LossPolicy(seed, LossRate, DuplicateRate, ReorderWindow);
            ServerTransport = new LossyLoopbackServerTransport(Policy);
            Server = new NetServer(ServerTransport,
                new ServerSimulation(new HeightfieldMoveSolver(FlatGround)), Spawn, Level);
            Zombies = new ZombieSystem(
                new[] { new ZombieTable { Name = "Civilian", Health = 100, Damage = 10 } },
                new List<NavBound>
                {
                    new()
                    {
                        Center = new Vector3(0, 140, 0),
                        Size = new Vector3(400, 300, 400),
                        MaxZombies = byte.MaxValue,
                    },
                },
                FlatGround);
            ZombieHost = new ZombieHost(Zombies, Server);
        }

        // Which name currently holds each server-assigned player id. The soak's own record of the
        // recycling it is provoking, and what turns "the rosters agree" into "and they name the right
        // people" — a tombstone bug shows up as the previous holder's name on a live avatar.
        public readonly Dictionary<byte, string> HolderOfId = new();

        // Every name each id has ever belonged to. PlayerIdPool hands out its 254 never-rented ids
        // before it recycles anything, so a soak with eight peers and a few dozen joins would never
        // reuse one — and the tombstones, the quarantine and the gesture floor, which all exist for
        // reuse, would go untested while the test still passed. This is what proves the run reached it.
        public readonly Dictionary<byte, HashSet<string>> EverHeldBy = new();

        public void ClaimId(byte id, string name)
        {
            HolderOfId[id] = name;
            if (!EverHeldBy.TryGetValue(id, out HashSet<string>? names))
                EverHeldBy[id] = names = new HashSet<string>();
            names.Add(name);
        }

        public int RecycledIds => EverHeldBy.Values.Count(names => names.Count > 1);

        public Peer Add(string name, bool stable)
        {
            var peer = new Peer(this, name, stable);
            Peers.Add(peer);
            return peer;
        }

        public void Pump(int rounds = 1)
        {
            for (int i = 0; i < rounds; i++)
            {
                Now += ServerSimulation.TickRate;
                Server.Update(Now);

                // The server's accepted swings for this tick, before the clients consume anything: the
                // list is the tick's output and is cleared on the next Step.
                foreach (PlayerGestureEvent gesture in Server.Gestures)
                {
                    if (HolderOfId.TryGetValue(gesture.PlayerId, out string? thrower))
                        AcceptedSwings.Add((thrower, Server.Tick));
                }

                foreach (Peer peer in Peers)
                    peer.Update(Now);
            }
        }

        // The server's own roster: the ids it currently considers joined.
        public HashSet<byte> ServerRoster()
        {
            var ids = new HashSet<byte>();
            Server.ForEachJoinedConnection((id, _, _) => ids.Add(id));
            return ids;
        }

        // Compares every connected peer's world against the server's. Called at a quiescence point, so
        // a disagreement here is a real divergence rather than a datagram still in flight.
        //
        // Collects into `failures` instead of asserting, for two reasons: one corrupted roster usually
        // corrupts several peers at once and the first Assert would hide the rest, and a divergence
        // that REPAIRS itself later is still a divergence worth naming — a run that threw at the first
        // one could never show that it also recovered.
        public void Check(List<string> failures, string when)
        {
            HashSet<byte> roster = ServerRoster();

            foreach (Peer peer in Peers)
            {
                if (peer.Client is not { Joined: true } client)
                    continue;

                // 1. The roster agrees with the server's, exactly: nobody missing, nobody left over.
                var expected = new HashSet<byte>(roster);
                expected.Remove(client.PlayerId);
                var held = new HashSet<byte>(client.Remotes.Keys);
                if (!expected.SetEquals(held))
                {
                    failures.Add($"[{when}] {peer.Name} sees "
                        + $"[{string.Join(",", held.OrderBy(x => x))}] but the server has "
                        + $"[{string.Join(",", expected.OrderBy(x => x))}]");
                }

                // 2. No ghost avatar: every id held names the person who actually holds it now. A failed
                // tombstone shows up here as a PREVIOUS holder's name sitting on a live id.
                foreach ((byte id, RemotePlayer remote) in client.Remotes)
                {
                    if (!HolderOfId.TryGetValue(id, out string? holder))
                        failures.Add($"[{when}] {peer.Name} holds id {id}, which was never issued");
                    else if (holder != remote.Name)
                        failures.Add($"[{when}] {peer.Name} thinks id {id} is '{remote.Name}'; "
                            + $"it is '{holder}'");
                }

                // 3. No zombie stands back up after it is dead.
                List<ushort> standingDead = peer.Standing.Where(KilledZombies.Contains).ToList();
                if (standingDead.Count > 0)
                    failures.Add($"[{when}] {peer.Name} has dead zombies standing: "
                        + $"[{string.Join(",", standingDead)}]");
            }
        }
    }

    // One client, plus the client-side state a real session keeps on top of NetClient.
    private sealed class Peer
    {
        private readonly Soak _soak;
        private readonly string _label;
        private int _incarnation;

        // A churning peer takes a NEW name every time it reconnects. That is what makes a stale avatar
        // visible: if a tombstone fails and an id keeps its previous occupant, the name on it is the
        // name of an incarnation that has gone, and the roster check below says so by name rather than
        // silently comparing a live player against themselves.
        public string Name => Stable ? _label : $"{_label}#{_incarnation}";

        public readonly bool Stable;
        public NetClient? Client;
        private LossyLoopbackClientTransport? _transport;
        private uint _frame;
        private byte _swing;

        // Zombies this peer believes are standing. The rule applied here is ZombiesView's
        // (src/Net/ZombiesView.cs): a region's list SpawnOrResets what it names, a kill removes and is
        // remembered forever, and a list that arrives after a kill may not stand the corpse back up.
        //
        // Mirrored rather than driven directly because the production applier is a Godot Node and this
        // suite has no engine — the server half (ZombieHost) IS the real code, and tests/Runtime
        // covers the Node. What the mirror buys is that the ORDERING the Node has to survive is
        // produced here for real instead of being hand-built one case at a time.
        public readonly HashSet<ushort> Standing = new();
        private readonly HashSet<ushort> _killed = new();

        // How many times a region list arrived naming a zombie this peer had ALREADY been told was
        // dead. This is the hazard counter, not a failure counter: it is exactly the ordering the
        // tombstone exists to absorb, and a soak in which it never happened would be proving nothing
        // about the tombstone however green it went.
        public int ListsNamingTheDead;

        // Swings played on each remote, as (thrower name, tick). Ticks, so a replay is visible as a
        // duplicate rather than being folded into a count.
        public readonly List<(string Thrower, uint Tick)> Played = new();


        public Peer(Soak soak, string label, bool stable)
        {
            _soak = soak;
            _label = label;
            Stable = stable;
        }

        public bool Connected => Client != null;

        public void Connect()
        {
            _incarnation++;
            _transport = _soak.ServerTransport.CreateClient();
            Client = new NetClient(_transport, Name, Level);
            Client.OnUnhandledMessage = HandleZombieMessage;
            // A session reset means the host may be a different one, numbering from zero: everything
            // keyed on its ids stops meaning anything, tombstones included (ZombiesView.ForgetSession).
            Client.OnSessionReset = () =>
            {
                Standing.Clear();
                _killed.Clear();
            };
        }

        public void Disconnect()
        {
            _transport?.Close();
            Client = null;
            _transport = null;
            Standing.Clear();
            _killed.Clear();
        }

        public void Update(double now)
        {
            if (Client == null)
                return;

            Client.Update(now);

            // Claim the id we were given, so the soak knows who holds what when a swing is accepted.
            if (Client.Joined && Client.PlayerId != 0)
                _soak.ClaimId(Client.PlayerId, Name);

            // Drain every avatar's one-shot gesture slot every tick, the way a renderer does. Polling
            // once per tick is what makes "exactly once" checkable at all: the slot holds one gesture,
            // and the swing cooldown is six ticks, so nothing is dropped by the polling itself.
            foreach ((byte id, RemotePlayer remote) in Client.Remotes)
            {
                EPlayerGesture gesture = remote.TakeGesture();
                if (gesture != EPlayerGesture.None)
                    Played.Add((remote.Name, _soak.Server.Tick));
                _ = id;
            }
        }

        public void Throw()
        {
            Client?.SendInput(new InputCommand(_frame++, 0, 0, jump: false, sprint: false, yaw: 0,
                pitch: 90, EPlayerStance.Stand, grounded: true, hasSwing: true,
                swingSequence: ++_swing, swingFist: EPlayerPunch.Left));
        }

        public void Idle()
        {
            Client?.SendInput(new InputCommand(_frame++, 0, 0, jump: false, sprint: false, yaw: 0,
                pitch: 90, EPlayerStance.Stand, grounded: true));
        }

        private void HandleZombieMessage(byte[] payload)
        {
            switch (NetMessages.TypeOf(payload))
            {
                case ENetMessage.ZombieList:
                    foreach (ZombieListing listing in ZombieNetMessages.ReadZombieList(payload).Listings)
                    {
                        // The tombstone is the whole point: reliable delivery does not order, so a
                        // region's list can arrive behind a kill for a zombie in it, and SpawnOrReset
                        // would stand the corpse back up with nothing left to remove it again. A death
                        // is final, so remembering it is enough.
                        if (_killed.Contains(listing.Id))
                        {
                            ListsNamingTheDead++;
                            continue;
                        }
                        Standing.Add(listing.Id);
                    }
                    break;
                case ENetMessage.ZombieKilled:
                    foreach (ushort id in ZombieNetMessages.ReadZombieKilled(payload).Ids)
                    {
                        _killed.Add(id);
                        Standing.Remove(id);
                    }
                    break;
            }
        }
    }

    // ------------------------------------------------------------------------------------------------

    // Eight clients, two thousand ticks, 10% loss, 5% duplication, a reorder window of three, with four
    // of the clients churning to force id recycling and zombies dying throughout. At the end the world
    // every client believes in must be the world the server has.
    [Fact]
    [Trait(TestCategories.Name, TestCategories.Soak)]
    public void EightClientsOverALossyReorderingWire_ConvergeOnTheServersWorld()
    {
        const ulong seed = 0x5EEDD00Du;
        var soak = new Soak(seed);

        // A population big enough that regional list chunking is exercised and kills keep arriving.
        var spawnpoints = new List<ZombieSpawnpointData>();
        for (int i = 0; i < 120; i++)
            spawnpoints.Add(new ZombieSpawnpointData(0, new Vector3(60f + (i % 12), 10f, i / 12 * 4f)));
        soak.Zombies.Spawn(spawnpoints, new Random(7));
        var alive = new List<ZombieInstance>(soak.Zombies.Zombies);

        for (int i = 0; i < ClientCount; i++)
            soak.Add(i < StableClients ? $"Stable{i}" : $"Churn{i}", stable: i < StableClients);

        foreach (Peer peer in soak.Peers)
            peer.Connect();
        soak.Pump(DrainRounds); // everyone in, rosters settled

        // Damage done mid-run does not always survive to the end: the final reconnects issue fresh
        // PlayerJoineds that paper over a roster a stale leave had corrupted, so a check only at the
        // finish line reads as green over a session that spent minutes wrong. The run is therefore
        // divided into phases, and every phase boundary is a QUIESCENCE checkpoint — churn and kills
        // stop, the wire drains, and the world is compared to the server's right there.
        var failures = new List<string>();
        int nextKill = 0;
        for (int tick = 0; tick < Ticks; tick++)
        {
            if (tick > 0 && tick % CheckpointEvery == 0)
            {
                soak.Pump(DrainRounds);
                soak.Check(failures, $"tick {tick}");
            }

            foreach (Peer peer in soak.Peers)
            {
                if (!peer.Connected)
                    continue;
                // Every stable peer punches on its own phase, well outside the six-tick cooldown, so
                // every swing thrown is a swing the server accepts.
                if (peer.Stable && tick % 12 == soak.Peers.IndexOf(peer))
                    peer.Throw();
                else
                    peer.Idle();
            }

            // A zombie dies every eight ticks, so kills keep overtaking region lists all run long.
            if (tick % 8 == 0 && nextKill < alive.Count)
            {
                ZombieInstance zombie = alive[nextKill++];
                soak.Zombies.Damage(zombie, 1000, byte.MaxValue, Array.Empty<ZombiePlayerView>());
                soak.ZombieHost.ReportKilled(zombie);
                soak.KilledZombies.Add(zombie.Id);
            }

            // The churn: the unstable half leaves and rejoins on staggered prime phases, so PlayerLeft
            // and PlayerJoined for the SAME id are in flight together.
            //
            // The cadence is deliberately fast. PlayerIdPool hands out all 254 never-rented ids before
            // it recycles any, so a leisurely churn would produce a few dozen joins, reuse nothing, and
            // leave the tombstones, the quarantine and the gesture floor — every guard that exists FOR
            // reuse — untouched while this test still passed. At this rate the run makes several
            // hundred admissions, exhausts the never-rented set, and spends its second half handing out
            // ids that somebody else has already held. The assertion at the end checks that it did.
            for (int i = StableClients; i < ClientCount; i++)
            {
                Peer peer = soak.Peers[i];
                int phase = 7 + ((i - StableClients) * 2); // 7, 9, 11, 13
                if (tick % phase == 0)
                {
                    if (peer.Connected)
                        peer.Disconnect();
                    else
                        peer.Connect();
                }
            }

            soak.Pump();
        }

        // Everyone still out comes back, and the wire is drained: what follows is a claim about a
        // settled session, not about a snapshot taken mid-delivery.
        foreach (Peer peer in soak.Peers)
            if (!peer.Connected)
                peer.Connect();
        soak.Pump(DrainRounds * 4);

        string diagnostics = $"seed 0x{seed:X}; {soak.Policy}";

        // The scenario has to have HAPPENED. A soak that reordered nothing looks exactly like a passing
        // one, and a rate that silently stopped being applied would make this whole test a no-op.
        Assert.True(soak.Policy.Dropped > 0, $"nothing was ever dropped — {diagnostics}");
        Assert.True(soak.Policy.Duplicated > 0, $"nothing was ever duplicated — {diagnostics}");
        Assert.True(soak.Policy.Delayed > 0, $"no reliable frame was ever retransmit-delayed — {diagnostics}");
        Assert.True(soak.Policy.Reordered > 0, $"nothing was ever delivered out of order — {diagnostics}");
        Assert.True(soak.AcceptedSwings.Count > 100,
            $"only {soak.AcceptedSwings.Count} swings were accepted — {diagnostics}");

        // And the run reached id recycling, which is what the tombstones and the quarantine are for.
        // Without this the churn could quietly slow down — or PlayerIdPool's never-rented set could
        // grow — and every guard below would go untested while the test still went green.
        Assert.True(soak.RecycledIds > 0,
            $"no player id was ever handed to a second player, so nothing here tested reuse — "
            + $"{soak.EverHeldBy.Count} ids issued, {diagnostics}");

        // And a region list really did arrive behind a kill for a zombie in it — the ordering the
        // zombie tombstone absorbs. Without this the tombstone assertion below could hold vacuously.
        int listsNamingTheDead = soak.Peers.Sum(p => p.ListsNamingTheDead);
        Assert.True(listsNamingTheDead > 0,
            $"no zombie list ever arrived behind a kill for a zombie in it — {diagnostics}");

        foreach (Peer peer in soak.Peers)
            Assert.True(peer.Client!.Joined, $"{peer.Name} never rejoined — {diagnostics}");

        // Roster, ghost avatars and standing corpses — the same three checks every phase boundary ran,
        // now on the settled session everybody is back in.
        soak.Check(failures, "final");
        Assert.True(failures.Count == 0,
            $"{failures.Count} divergence(s) — {diagnostics}\n  "
            + string.Join("\n  ", failures.Take(20)));

        // 4. Every accepted swing played exactly once on every peer that was there for the whole run.
        //
        // Restricted to the stable four on purpose: a peer that was disconnected when a swing was
        // thrown is owed nothing, and asserting otherwise would be asserting that leaving a server
        // still shows you what happened while you were gone.
        var stable = soak.Peers.Where(p => p.Stable).ToList();
        foreach (Peer thrower in stable)
        {
            List<uint> accepted = soak.AcceptedSwings
                .Where(s => s.Thrower == thrower.Name).Select(s => s.Tick).ToList();

            foreach (Peer observer in stable)
            {
                if (ReferenceEquals(observer, thrower))
                {
                    // The thrower is never told about their own swing: their client played it when the
                    // button went down, and replaying it a round trip later restarts the animation.
                    Assert.True(observer.Played.All(p => p.Thrower != thrower.Name),
                        $"{observer.Name} was replayed their own swing — {diagnostics}");
                    continue;
                }

                List<uint> played = observer.Played
                    .Where(p => p.Thrower == thrower.Name).Select(p => p.Tick).ToList();

                // Never twice. A duplicated PlayerGesture that beat the tick guard would land here.
                Assert.True(played.Count == played.Distinct().Count(),
                    $"{observer.Name} played one of {thrower.Name}'s swings twice — {diagnostics}");

                // And never fewer: every swing the server accepted reached every peer that was present.
                Assert.True(played.Count == accepted.Count,
                    $"{observer.Name} played {played.Count} of {thrower.Name}'s {accepted.Count} "
                    + $"accepted swings — {diagnostics}");
            }
        }
    }

    // The transport is test infrastructure, so it gets its own tests: a soak that passed because the
    // pipe quietly delivered everything in order would be worse than no soak at all.
    [Fact]
    public void APerfectPolicyDeliversEverythingInOrder()
    {
        var policy = new LossPolicy(1, lossRate: 0, duplicateRate: 0, reorderWindow: 0);
        var transport = new LossyLoopbackServerTransport(policy);
        LossyLoopbackClientTransport client = transport.CreateClient();

        Assert.True(transport.TryReceive(out ServerTransportEvent connected));
        Assert.Equal(ETransportEvent.Connected, connected.Type);

        for (byte i = 1; i <= 10; i++)
            connected.Connection.Send(new byte[] { i }, ESendType.Reliable);

        var seen = new List<byte>();
        for (int i = 0; i < 12; i++)
        {
            client.Update(i);
            while (client.TryReceive(out byte[] payload))
                seen.Add(payload[0]);
        }

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, seen);
        Assert.Equal(0, policy.Dropped);
        Assert.Equal(0, policy.Reordered);
    }

    [Fact]
    public void AnUnreliableDatagramCanBeLostAndAReliableOneNeverIs()
    {
        var policy = new LossPolicy(42, lossRate: 1.0, duplicateRate: 0, reorderWindow: 2);
        var transport = new LossyLoopbackServerTransport(policy);
        LossyLoopbackClientTransport client = transport.CreateClient();
        Assert.True(transport.TryReceive(out ServerTransportEvent connected));

        connected.Connection.Send(new byte[] { 1 }, ESendType.Unreliable);
        connected.Connection.Send(new byte[] { 2 }, ESendType.Reliable);

        var seen = new List<byte>();
        for (int i = 0; i < 20; i++)
        {
            client.Update(i);
            while (client.TryReceive(out byte[] payload))
                seen.Add(payload[0]);
        }

        // The unreliable one is gone; the reliable one arrived, late — which is exactly the shape of a
        // frame that was lost and retransmitted, and the reason the guards upstream exist.
        Assert.Equal(new byte[] { 2 }, seen);
        Assert.Equal(1, policy.Dropped);
        Assert.Equal(1, policy.Delayed);
    }

    // The window bounds displacement in SLOTS, which is only the same thing as displacement in
    // delivery position when at most one datagram is sent per slot — so that is how this sends, one
    // per pump, the way a session sends one batch per tick. Firing sixty into a single slot and then
    // measuring positions would be measuring how the ties inside that slot happened to break.
    [Fact]
    public void TheReorderWindowIsABoundRatherThanAShuffle()
    {
        var policy = new LossPolicy(9, lossRate: 0, duplicateRate: 0, reorderWindow: 3);
        var transport = new LossyLoopbackServerTransport(policy);
        LossyLoopbackClientTransport client = transport.CreateClient();
        Assert.True(transport.TryReceive(out ServerTransportEvent connected));

        var seen = new List<byte>();
        for (int i = 0; i < 80; i++)
        {
            if (i < 60)
                connected.Connection.Send(new byte[] { (byte)i }, ESendType.Unreliable);
            client.Update(i);
            while (client.TryReceive(out byte[] payload))
                seen.Add(payload[0]);
        }

        Assert.Equal(60, seen.Count);                        // no loss configured, so nothing vanished
        Assert.True(policy.Reordered > 0, "the window never actually reordered anything");
        // Bounded: nothing may be overtaken by more than the window's worth of later datagrams.
        for (int at = 0; at < seen.Count; at++)
            Assert.True(Math.Abs(seen[at] - at) <= policy.ReorderWindow,
                $"datagram {seen[at]} was delivered at position {at}, outside the window");
    }

    [Fact]
    public void ADuplicatedDatagramArrivesTwice()
    {
        var policy = new LossPolicy(3, lossRate: 0, duplicateRate: 1.0, reorderWindow: 0);
        var transport = new LossyLoopbackServerTransport(policy);
        LossyLoopbackClientTransport client = transport.CreateClient();
        Assert.True(transport.TryReceive(out ServerTransportEvent connected));

        connected.Connection.Send(new byte[] { 7 }, ESendType.Reliable);

        var seen = new List<byte>();
        for (int i = 0; i < 6; i++)
        {
            client.Update(i);
            while (client.TryReceive(out byte[] payload))
                seen.Add(payload[0]);
        }

        Assert.Equal(new byte[] { 7, 7 }, seen);
        Assert.Equal(1, policy.Duplicated);
    }

    [Theory]
    [InlineData(-0.1, 0.0, 0)]
    [InlineData(1.1, 0.0, 0)]
    [InlineData(0.0, -0.1, 0)]
    [InlineData(0.0, 1.1, 0)]
    [InlineData(0.0, 0.0, -1)]
    public void AnImpossiblePolicyIsRefused(double loss, double duplicate, int window)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LossPolicy(1, loss, duplicate, window));
    }

    [Fact]
    public void ClosingTheTransportDropsWhatWasStillInFlight()
    {
        var policy = new LossPolicy(11, lossRate: 0, duplicateRate: 0, reorderWindow: 3);
        var transport = new LossyLoopbackServerTransport(policy);
        LossyLoopbackClientTransport client = transport.CreateClient();
        Assert.True(transport.TryReceive(out ServerTransportEvent connected));

        connected.Connection.Send(new byte[] { 1 }, ESendType.Reliable);
        Assert.False(transport.Idle);

        transport.Close();
        Assert.True(transport.Idle);

        client.Update(1);
        Assert.False(client.TryReceive(out _));
    }
}
