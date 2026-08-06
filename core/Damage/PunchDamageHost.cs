using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Net;
using UnturnedGodot.Player;
using UnturnedGodot.Zombies;

namespace UnturnedGodot.Damage;

// The server half of PlayerEquipment.punch: every swing the simulation accepted this tick is cast into
// the world, and whatever it found takes damage.
//
// Deliberately hung off the SWING rather than off the input's attack flags. The simulation has already
// judged which presses became swings — the cooldown, the prone gate, the rate limit and the
// one-swing-one-number rule all live there — so a swing reaching this point is one the server has
// already agreed happened, exactly once. Damage is then a pure function of it plus the player's own
// replicated state, which is why none of the maths below needs anything from the client.
//
// The ray is cast HERE rather than on the client. Unturned does it the other way round (the client
// raycasts and sends the result, and the server checks only that the point is within six metres),
// because in Unity the server has no view of the client's local world. This port's server does: it
// builds the same terrain and object collision, and it owns every zombie's position. Casting on the
// authoritative side is therefore both simpler — nothing new on the wire — and stricter, and the six
// metre rule is kept anyway as the ceiling it is.
public sealed class PunchDamageHost
{
    private readonly NetServer _server;

    // The zombie brain, when the level has one. A map with no zombie data still has breakable rubble
    // and resources standing on it, and a player whose fists work — so the punch host exists either way
    // and simply finds no bodies to hit.
    private readonly ZombieSystem? _zombies;
    private readonly ZombieHost? _zombieHost;
    private readonly List<ZombiePlayerView> _views = new();
    private readonly Action<byte, PlayerMoveState, ITransportConnection> _collectPlayer;

    // Where the punch's ray meets the solid world, and what stands between two points. Both are physics
    // queries the host cannot make itself; a Godot session installs them, and a pure test leaves them
    // null and gets an empty world.
    public PunchTargeting.WorldRaycast? WorldRaycast { get; set; }
    public PunchTargeting.LineBlocked? LineBlocked { get; set; }

    // The level's breakable furniture. Settable rather than a constructor argument because the two hosts
    // learn it at different moments: a dedicated server builds it alongside the collision it is derived
    // from, while an interactive session's object streamer is still reading the map when the session
    // starts. Until it is set, a swing that lands on the world simply finds nothing there.
    public DamageableWorld? World { get; set; }

    // Raised after a swing resolves, whatever it found — including nothing. The runtime logs it; a HUD
    // or a hit marker is what it is really for, and it is the one place that knows both the swing and
    // its outcome.
    public event Action<PunchResult>? Resolved;

    // Raised when a swing lands on a surface that says what it is made of — DamageTool.ServerSpawnLegacyImpact,
    // as an event rather than a call, because what a host DOES with it differs: an interactive session
    // plays it locally and replicates it, and a dedicated server only replicates.
    //
    // Fires for a wall as readily as for a crate: this is not a damage notification.
    public event Action<PunchImpact>? Impacted;

    public PunchDamageHost(NetServer server, ZombieSystem? zombies, ZombieHost? zombieHost = null,
        DamageableWorld? world = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _zombies = zombies;
        _zombieHost = zombieHost;
        World = world;
        _collectPlayer = CollectPlayer;
        server.OnTick += Tick;
    }

    private void CollectPlayer(byte id, PlayerMoveState state, ITransportConnection connection) =>
        _views.Add(new ZombiePlayerView(id, state.Position, state.Stance, state.Moving));

    // Runs on the server's fixed tick, after the step that produced the gestures.
    public void Tick(uint tick)
    {
        IReadOnlyList<PlayerGestureEvent> gestures = _server.Gestures;
        if (gestures.Count == 0)
            return;

        // Gathered once per tick that has a swing in it, and not otherwise: the list is only needed so
        // a damaged zombie can be turned on its attacker, and most ticks contain no swing at all.
        _views.Clear();
        _server.ForEachJoinedConnection(_collectPlayer);

        for (int i = 0; i < gestures.Count; i++)
        {
            PlayerGestureEvent gesture = gestures[i];
            if (gesture.Gesture is not (EPlayerGesture.PunchLeft or EPlayerGesture.PunchRight))
                continue;
            if (!_server.TryGetPlayerState(gesture.PlayerId, out PlayerMoveState state))
                continue; // they left between the step and here
            Swing(gesture, state);
        }
    }

    // One fist, from the eyes it was thrown from.
    //
    // The ANGLES and the STANCE come off the gesture — the input frame that announced the swing — while
    // the POSITION comes off the tick's authoritative state. That split is deliberate. A swing is
    // accepted the moment its datagram lands, so a burst can leave the simulation replaying frames from
    // a third of a second earlier when the gesture finally goes out, and a player turns far enough in
    // that time to miss what they were plainly looking at. Angles are the client's word wherever they
    // are read from, so taking them from the swing's own frame costs nothing; the position is the one
    // thing a speed budget validates, and a punch is not the place to go around it.
    private void Swing(in PlayerGestureEvent gesture, in PlayerMoveState state)
    {
        byte playerId = gesture.PlayerId;
        Vector3 origin = PunchAim.Origin(state.Position, gesture.Stance);
        Vector3 forward = PunchAim.Forward(NetAngles.DequantizeYaw(gesture.Yaw),
            PunchAim.PitchFromWire(NetAngles.DequantizePitch(gesture.Pitch)));

        // Read once. The ledger is settable — an interactive session installs it mid-load — and a hit
        // that names an index has to be applied to the ledger that produced it, not to whatever the
        // property says a few lines later.
        DamageableWorld? world = World;
        PunchHit hit = PunchTargeting.Resolve(origin, forward, PunchDamageData.Reach,
            _zombies?.Zombies ?? Array.Empty<ZombieInstance>(), world, WorldRaycast, LineBlocked);

        // The original's own sanity check on where the hit landed. It cannot fail for a ray this host
        // cast itself — the reach is 1.75 m — and it is applied anyway, because the day a hit arrives
        // from somewhere else is the day it matters, and a rule that is only there when convenient is
        // not a rule.
        if (hit.Exists && !PunchTargeting.IsWithinServerRange(origin, hit.Point))
            hit = PunchHit.None;

        // The impact goes out BEFORE the damage, and independently of it. PlayerEquipment.punch spawns it
        // off the material name alone, above the whole `info.type` ladder, so a wall that takes no damage
        // still cracks — and a target that takes damage cracks whether or not the damage killed it.
        if (hit.HasSurface)
            Impacted?.Invoke(new PunchImpact(playerId, hit.Point, hit.Normal, hit.Surface));

        PunchResult result = hit.Kind switch
        {
            EPunchTargetKind.Zombie => DamageZombie(playerId, hit, forward),
            EPunchTargetKind.Resource or EPunchTargetKind.Object => DamagePlacement(playerId, hit, world!),
            _ => new PunchResult(playerId, hit, 0, Destroyed: false),
        };
        Resolved?.Invoke(result);
    }

    private PunchResult DamageZombie(byte playerId, in PunchHit hit, Vector3 direction)
    {
        ZombieInstance? zombie = _zombies?.Find((ushort)hit.Id);
        if (zombie == null)
            return new PunchResult(playerId, hit, 0, Destroyed: false);

        ushort amount = PunchDamageResolver.Zombie(hit.Limb, direction, ZombieHitbox.ForwardOf(zombie));
        bool killed = _zombies!.Damage(zombie, amount, playerId, _views);
        if (killed)
            _zombieHost?.ReportKilled(zombie);
        return new PunchResult(playerId, hit, amount, killed);
    }

    // `world` is the ledger the hit came out of, and it cannot be null here: Resolve only ever names a
    // placement when it had one to look the point up in.
    private static PunchResult DamagePlacement(byte playerId, in PunchHit hit, DamageableWorld world)
    {
        ushort amount = world.PunchDamageTo(hit.Id);
        bool destroyed = world.Damage(hit.Id, amount);
        return new PunchResult(playerId, hit, amount, destroyed);
    }
}

// What one swing did. `Amount` is zero both for a swing that found nothing and for one that found
// something a fist cannot hurt — a pine, whose asset does not say Vulnerable_To_Fists — and the two are
// told apart by whether the hit exists.
public readonly record struct PunchResult(byte PlayerId, PunchHit Hit, ushort Amount, bool Destroyed);

// Where a fist met the world and what that was made of: the arguments of ReceiveSpawnLegacyImpact, plus
// who threw it. `Surface` is a physics-material name, which is what both the sound and the mark are
// resolved through, and it is never empty here — a swing with no surface raises no impact at all.
public readonly record struct PunchImpact(byte PlayerId, Vector3 Point, Vector3 Normal, string Surface);
