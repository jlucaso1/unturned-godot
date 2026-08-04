using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Net;
using UnturnedGodot.Zombies;

namespace UnturnedGodot;

// Renders the replicated zombie population: real Zombie_Client bodies (built once per look and cheaply
// cloned), positioned from the server's ZombieList and interpolated through the same snapshot buffer the
// remote players use. Animation follows Zombie.cs exactly: Move_{move}/Idle_{idle} variants with the
// crawler (Move_4/Idle_3) and sprinter (Move_5/Idle_4) overrides, Attack_0..4 swings, and the replicated
// per-zombie scale band (megas 1.45-1.55, everyone else 0.95-1.05).
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class ZombiesView : Node3D
{
    private const float AnimateWithin = 100f;  // skeletons past this stop sampling (Unturned's far
                                               // zombie regions deactivate entirely)

    private sealed class ZombieAvatar
    {
        public required Node3D Root;
        public CharacterSkeleton? Rig;
        public EZombieSpeciality Speciality;
        public byte Move;
        public byte Idle;
        public EZombieState State;
        public byte Bound;         // the zombie's home region (from its ZombieList header)
        public bool Streaming;     // any ZombieStates received yet (idle zombies never stream)
        public double StartleUntil; // while set, the wake-up roar plays before the state clip
        public double NextGroan;    // Zombie.cs's groan loop clock
        public Vector3 TargetPosition; // Zombie.tellState's interpolation target
        public float TargetYaw;
        public double NextSwing;    // client-side swing clock while the state stays Attack

        // C#-side mirrors of engine state, so the per-frame loop never crosses into Godot for a value it
        // already knows, and never writes one that hasn't changed. With the whole population asleep the
        // loop makes zero interop calls; the values written are exactly the ones written before.
        public Vector3 KnownPosition;  // Root.Position as last written
        public float AppliedYaw;       // Root.RotationDegrees.Y as last written
        public bool AnimationActive = true; // rig.ProcessMode as last written (Inherit on spawn)
    }

    private string _unturnedPath = "";
    private NetClient? _client;
    private OneShotAudio? _audio;
    private readonly Dictionary<ushort, ZombieAvatar> _avatars = new();
    private readonly List<ushort> _leaving = new();
    private Node3D? _normalTemplate;
    private Node3D? _megaTemplate;
    private bool _templatesWarmed;
    private readonly RandomNumberGenerator _rng = new();
    private IReadOnlyList<NavBound> _navBounds = System.Array.Empty<NavBound>();
    private System.Func<Vector3>? _localPosition;
    private byte _localBound = LevelNavigationData.NoBound;

    // Zombies this client has been told are dead. Reliable delivery retransmits and de-duplicates but
    // does NOT order — the same reason a replicated gesture carries its tick — so a region's zombie list
    // can arrive after a kill for a zombie in it, and SpawnOrReset would then stand the corpse back up
    // with nothing left to remove it again. A death is final, so remembering it is enough.
    //
    // Kept for the life of the view rather than dropped on a region change. Ids are handed out once at
    // load and run across every bound (ZombieSystem numbers the whole level from a single counter) and
    // nothing recycles one — Remove takes the instance out and no respawn puts another in its place — so
    // an id names exactly one zombie for the session and a tombstone can never suppress a live one. A
    // stale list can also outlive a round trip through a region: leave and come back before the pre-kill
    // list for it is delivered, and clearing here would be the difference between a corpse staying down
    // and standing back up with nothing left to remove it. A session's dead are a few thousand ushorts.
    private readonly HashSet<ushort> _killed = new();

    public static ZombiesView Create(NetClient client, string unturnedPath, OneShotAudio? audio,
        IReadOnlyList<NavBound> navBounds, System.Func<Vector3> localPosition)
    {
        var view = new ZombiesView
        {
            Name = "Zombies",
            _client = client,
            _unturnedPath = unturnedPath,
            _audio = audio,
            _navBounds = navBounds,
            _localPosition = localPosition,
        };
        client.OnUnhandledMessage += view.Handle;
        return view;
    }

    public override void _ExitTree()
    {
        if (_client != null)
            _client.OnUnhandledMessage -= Handle;

        // Templates are deliberately not children (they must never render); unlike their shared Resources,
        // unparented Nodes are not reclaimed by tree teardown, so release them explicitly with the view.
        _normalTemplate?.Free();
        _megaTemplate?.Free();
        _normalTemplate = null;
        _megaTemplate = null;
    }

    // Character import is synchronous because its final phase creates Godot rendering resources. Do it
    // explicitly while Main's loading screen is still up, rather than on the first ZombieList received
    // when the player crosses into a populated city. Normal and mega share the one expensive import.
    public void WarmupTemplates()
    {
        if (_templatesWarmed)
            return;
        _templatesWarmed = true; // a failed import must not be retried for every zombie in the packet
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        (_normalTemplate, _megaTemplate) = CharacterModel.BuildZombieTemplates(_unturnedPath);
        double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Log.Print($"[performance] zombie visual templates warmed during loading in {elapsedMs:0.0} ms " +
            $"(normal={_normalTemplate != null}, mega={_megaTemplate != null}).");
    }

    // Zombie.PlayOneShot: volume 0.5, linear rolloff to 32 m, pitch by speciality (megas growl low).
    private void PlayVoice(ZombieAvatar avatar, string group)
    {
        (float minPitch, float maxPitch) = avatar.Speciality == EZombieSpeciality.Mega
            ? (0.5f, 0.7f)
            : (0.9f, 1.1f);
        _audio?.Play(group, avatar.KnownPosition + Vector3.Up, volumeScale: 0.5f, maxDistance: 32f,
            minPitch, maxPitch);
    }

    private void Handle(byte[] payload)
    {
        // NetClient hands unhandled messages here without decoding them, so this reads straight off the
        // wire and owns its own guard — same rule as NetServer.HandleMessage: decode inside, act outside,
        // so a truncated zombie payload costs a dropped datagram and a fault in the view still surfaces.
        if (!MalformedPacket.TryDecode(payload, ReadType, out ENetMessage type))
        {
            MalformedPacketsDropped++;
            return;
        }

        switch (type)
        {
            case ENetMessage.ZombieList:
                if (!MalformedPacket.TryDecode(payload, ReadZombieList, out var list))
                {
                    MalformedPacketsDropped++;
                    break;
                }

                (byte bound, List<ZombieListing> listings) = list;
                // A reliable list can outrun the player: if it names a region the local player is NOT
                // standing in right now (computed fresh — the cached bound lags a frame), it is a late
                // delivery for a region already left. Drop it: instancing it would resurrect avatars
                // nobody prunes until the next transition. A dropped edge-flicker list self-heals — the
                // server resends on its next observed transition.
                if (_localPosition != null
                    && bound != LevelNavigationData.TryGetBound(_navBounds, _localPosition()))
                    break;
                foreach (ZombieListing listing in listings)
                    SpawnOrReset(listing, bound);
                break;
            case ENetMessage.ZombieStates:
                if (!MalformedPacket.TryDecode(payload, ReadZombieStates, out var update))
                {
                    MalformedPacketsDropped++;
                    break;
                }

                double now = NetworkManager.Now;
                foreach (ZombieSnapshotState state in update.States)
                    Push(state, now);
                break;
            case ENetMessage.ZombieKilled:
                if (!MalformedPacket.TryDecode(payload, ReadZombieKilled, out var killed))
                {
                    MalformedPacketsDropped++;
                    break;
                }

                // The bound is not checked the way the list's is. A kill for a region already left names
                // an avatar DropForeignRegions is about to free anyway, and one that arrives while the
                // player is mid-transition still names a zombie that is genuinely gone — the one thing
                // that must not happen is leaving a dead zombie standing because the message looked
                // late.
                foreach (ushort id in killed.Ids)
                    Kill(id);
                break;
        }
    }

    // ZombieManager's death: the avatar goes. Unturned plays a ragdoll here (Zombie.askDamage hands
    // askDamage's force to the ragdoll and the body falls); there is no ragdoll in this port, so the
    // body is simply freed, which is what its own dead-zombie cleanup does once the ragdoll expires.
    private void Kill(ushort id)
    {
        // Remembered whether or not an avatar exists for it: a kill that arrives BEFORE the list that
        // would have spawned this zombie is precisely the case the tombstone is for.
        _killed.Add(id);
        if (!_avatars.Remove(id, out ZombieAvatar? avatar))
            return; // never spawned here, or already removed on a region change
        avatar.Root.QueueFree();
    }

    // Zombie datagrams that reached a decoder and did not survive it; see NetServer.MalformedPacketsDropped.
    public long MalformedPacketsDropped { get; private set; }

    // Cached so a method group does not allocate a delegate on every received message.
    private static readonly System.Func<byte[], ENetMessage> ReadType = NetMessages.TypeOf;
    private static readonly System.Func<byte[], (byte Bound, List<ZombieListing> Listings)> ReadZombieList =
        ZombieNetMessages.ReadZombieList;
    private static readonly System.Func<byte[], (uint Tick, List<ZombieSnapshotState> States)> ReadZombieStates =
        ZombieNetMessages.ReadZombieStates;
    private static readonly System.Func<byte[], (byte Bound, List<ushort> Ids)> ReadZombieKilled =
        ZombieNetMessages.ReadZombieKilled;

    private void SpawnOrReset(in ZombieListing listing, byte bound)
    {
        if (_killed.Contains(listing.Id))
            return; // a list that outran the death it predates
        if (!_avatars.TryGetValue(listing.Id, out ZombieAvatar? avatar))
            _avatars[listing.Id] = avatar = Spawn(listing);

        avatar.Bound = bound;
        avatar.Speciality = listing.Speciality;
        avatar.Move = listing.Move;
        avatar.Idle = listing.Idle;
        avatar.Root.Position = listing.Position;
        avatar.Root.RotationDegrees = new Vector3(0, NetAngles.DequantizeYaw(listing.Yaw), 0);
        avatar.KnownPosition = listing.Position;
        avatar.AppliedYaw = NetAngles.DequantizeYaw(listing.Yaw);
        avatar.TargetPosition = listing.Position;
        avatar.TargetYaw = avatar.AppliedYaw;
        avatar.Rig?.Play(IdleClip(avatar));
    }

    private ZombieAvatar Spawn(in ZombieListing listing)
    {
        bool isMega = listing.Speciality == EZombieSpeciality.Mega;
        Node3D? template = Template(isMega);
        Node3D body = CharacterModel.Clone(template) ?? Placeholder();

        var root = new Node3D { Name = $"Zombie_{listing.Id}" };
        // Zombie.cs randomizes each zombie's visual scale locally on spawn.
        // Zombie.cs's spawn scale, from the one definition the punch hitbox is sized against too.
        float scale = ZombieBody.ModelScaleFor(isMega);
        root.Scale = Vector3.One * _rng.RandfRange(
            scale - ZombieBody.ModelScaleJitter, scale + ZombieBody.ModelScaleJitter);
        root.AddChild(body);
        AddChild(root);

        return new ZombieAvatar { Root = root, Rig = body as CharacterSkeleton };
    }

    private Node3D? Template(bool isMega)
    {
        // Defensive lazy fallback for callers that construct the view outside Main (or missing/corrupt
        // game data). The normal runtime has already warmed both looks before network polling resumes.
        if (!_templatesWarmed)
            WarmupTemplates();
        if (isMega)
            return _megaTemplate;
        return _normalTemplate;
    }

    private void Push(in ZombieSnapshotState state, double now)
    {
        if (!_avatars.TryGetValue(state.Id, out ZombieAvatar? avatar))
            return; // its ZombieList chunk hasn't arrived yet; the reliable channel will deliver it

        // Zombie.tellState: replication only refreshes the interpolation TARGET; OnUpdate lerps
        // the body toward it exponentially (INTERP_SPEED). No delay buffer, unlike players.
        avatar.TargetPosition = state.Position;
        avatar.TargetYaw = NetAngles.DequantizeYaw(state.Yaw);
        avatar.Streaming = true;

        if (state.State != avatar.State)
        {
            bool wokeUp = avatar.State == EZombieState.Idle && state.State == EZombieState.Chase;
            avatar.State = state.State;
            if (wokeUp && avatar.Rig is { } rig)
            {
                // Zombie.alert's startle: the wake-up roar plays first (the body already moves —
                // the server never pauses for it), then the state clip takes over.
                string startle = StartleClip(avatar);
                rig.Play(startle);
                avatar.StartleUntil = now
                    + (rig.Clips.TryGetValue(startle, out var clip) && clip.Length > 0f ? clip.Length : 0.5f);
                PlayVoice(avatar, "ZombieRoars");
            }
            else if (avatar.StartleUntil <= 0)
            {
                if (avatar.State == EZombieState.Attack)
                {
                    avatar.Rig?.Replay(AttackClip(avatar));
                    avatar.NextSwing = now + 1.0;
                    PlayVoice(avatar, "ZombieRoars"); // askAttack's swing roar
                }
                // Chase/Return/Idle clips are picked per-frame from the rendered motion.
            }
        }
    }

    public override void _Process(double delta)
    {
        long benchmarkStarted = Benchmark.RuntimeCounters.Start();
        double now = NetworkManager.Now;
        Vector3? eye = GetViewport()?.GetCamera3D()?.GlobalPosition;

        // ZombieManager.onBoundUpdated (IsLocalPlayer branch): when the LOCAL player leaves a nav
        // bound, the client destroys that region's avatars on its own — no server message involved.
        // The server independently notices the same transition and resends the region on re-entry.
        if (_localPosition != null)
        {
            byte bound = LevelNavigationData.TryGetBound(_navBounds, _localPosition());
            if (bound != _localBound)
            {
                _localBound = bound;
                DropForeignRegions();
            }
        }

        foreach (ZombieAvatar avatar in _avatars.Values)
        {
            bool visiblyMoving = false;
            if (avatar.Streaming)
            {
                // Zombie.OnUpdate's exponential interpolation (INTERP_SPEED = 10) toward the last
                // replicated target — no delay buffer, so remote zombies track as tightly as the
                // original's. Writes only happen while the pose is actually changing.
                float blend = MathF.Min(1f, (float)delta * 10f);
                float gap = avatar.KnownPosition.DistanceSquaredTo(avatar.TargetPosition);
                // The original compares each axis against 0.01 m. Treating 0.01 as a squared-distance
                // threshold accidentally made ours 0.1 m: after Startle ended, small but real snapshot
                // gaps kept selecting Idle and visibly delayed the transition into Chase.
                visiblyMoving = ZombieClientMotion.IsMoving(
                    avatar.TargetPosition, avatar.KnownPosition);
                if (gap > 1e-8f)
                {
                    avatar.KnownPosition = avatar.KnownPosition.Lerp(avatar.TargetPosition, blend);
                    avatar.Root.Position = avatar.KnownPosition;
                }
                float yawDelta = Mathf.Wrap(avatar.TargetYaw - avatar.AppliedYaw, -180f, 180f);
                if (MathF.Abs(yawDelta) > 0.01f)
                {
                    avatar.AppliedYaw = Mathf.Wrap(avatar.AppliedYaw + (yawDelta * blend), 0f, 360f);
                    avatar.Root.RotationDegrees = new Vector3(0, avatar.AppliedYaw, 0);
                }
            }

            if (avatar.StartleUntil > 0 && now >= avatar.StartleUntil)
            {
                avatar.StartleUntil = 0;
                avatar.Rig?.Play(ClipFor(avatar, visiblyMoving)); // the roar ended: current clip
            }
            else if (avatar.StartleUntil <= 0 && avatar.Rig != null && avatar.Streaming)
            {
                if (avatar.State == EZombieState.Attack)
                {
                    // The server swings every SwingInterval but no per-swing event is replicated
                    // (yet): re-trigger a fresh attack clip on the same cadence client-side so the
                    // zombie visibly keeps swinging instead of looping one frozen clip.
                    if (now >= avatar.NextSwing)
                    {
                        avatar.NextSwing = now + 1.0;
                        avatar.Rig.Replay(AttackClip(avatar));
                    }
                }
                else
                {
                    avatar.Rig.Play(ClipFor(avatar, visiblyMoving));
                }
            }

            // One camera-distance test drives both the groan loop and the animation gate (they share
            // the same radius), off the C#-side position — no engine round-trip.
            bool nearby = eye is { } cam
                && avatar.KnownPosition.DistanceSquaredTo(cam) <= AnimateWithin * AnimateWithin;

            // Zombie.cs's groan loop, only for visible (nearby) zombies: every 4-8 s (megas 2-4 s),
            // a standing zombie has a 20% chance to groan while a moving one always roars.
            if (nearby && now >= avatar.NextGroan)
            {
                avatar.NextGroan = now + (avatar.Speciality == EZombieSpeciality.Mega
                    ? _rng.RandfRange(2f, 4f)
                    : _rng.RandfRange(4f, 8f));
                bool moving = avatar.State is EZombieState.Chase or EZombieState.Return;
                if (moving)
                    PlayVoice(avatar, "ZombieRoars");
                else if (_rng.Randf() > 0.8f)
                    PlayVoice(avatar, "ZombieGroans");
            }

            // Far zombies keep their pose but stop sampling animation, like Unturned's inactive regions.
            // The mode is only pushed on transitions; re-asserting it every frame was an interop call
            // per zombie per frame.
            if (avatar.Rig is { } rig && eye != null && nearby != avatar.AnimationActive)
            {
                avatar.AnimationActive = nearby;
                rig.ProcessMode = nearby ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
            }
        }
        Benchmark.RuntimeCounters.Record(Benchmark.RuntimeCounters.Counter.ZombiesView, benchmarkStarted);
    }

    // Zombie.cs's animation selection: the move/idle split follows what the RENDERED body is
    // doing (the original client checks whether it is still approaching its interpolation target),
    // so a physically blocked zombie stands instead of treadmilling its walk cycle.
    private string ClipFor(ZombieAvatar avatar, bool visiblyMoving) => avatar.State switch
    {
        EZombieState.Attack => AttackClip(avatar),
        _ => visiblyMoving ? MoveClip(avatar) : IdleClip(avatar),
    };

    // sendZombieAttack's swing ids: crawlers swipe from the ground with Attack_5, sprinters lunge
    // with Attack_6..8, everyone else swings the standing Attack_0..4.
    private string AttackClip(ZombieAvatar avatar) => avatar.Speciality switch
    {
        EZombieSpeciality.Crawler => "Attack_5",
        EZombieSpeciality.Sprinter => $"Attack_{_rng.RandiRange(6, 8)}",
        _ => $"Attack_{_rng.RandiRange(0, 4)}",
    };

    private static string MoveClip(ZombieAvatar avatar) => avatar.Speciality switch
    {
        EZombieSpeciality.Crawler => "Move_4",
        EZombieSpeciality.Sprinter => "Move_5",
        _ => $"Move_{avatar.Move}",
    };

    private static string IdleClip(ZombieAvatar avatar) => avatar.Speciality switch
    {
        EZombieSpeciality.Crawler => "Idle_3",
        EZombieSpeciality.Sprinter => "Idle_4",
        _ => $"Idle_{avatar.Idle}",
    };

    // Zombie.alert's startle roll: crawlers roar with 3/6, sprinters with 4/5, everyone else 0..2.
    private string StartleClip(ZombieAvatar avatar) => avatar.Speciality switch
    {
        EZombieSpeciality.Crawler => _rng.Randf() < 0.5f ? "Startle_3" : "Startle_6",
        EZombieSpeciality.Sprinter => _rng.Randf() < 0.5f ? "Startle_4" : "Startle_5",
        _ => $"Startle_{_rng.RandiRange(0, 2)}",
    };

    // ZombieRegion.destroy on region change: free every avatar that is not of the CURRENT region —
    // the one just left, plus any region a late reliable list resurrected after we had already moved
    // on (the guard the original doesn't need, since its RPCs can't outrun its bound events).
    private void DropForeignRegions()
    {
        // The tombstones deliberately survive this: see _killed. Ids are level-global and never reused,
        // so nothing here can go stale, and a region the player re-enters may still have a pre-kill list
        // in flight.
        _leaving.Clear();
        foreach ((ushort id, ZombieAvatar avatar) in _avatars)
            if (avatar.Bound != _localBound)
                _leaving.Add(id);
        foreach (ushort id in _leaving)
        {
            _avatars[id].Root.QueueFree();
            _avatars.Remove(id);
        }
    }

    private static Node3D Placeholder() => new MeshInstance3D
    {
        Mesh = new CapsuleMesh { Radius = 0.4f, Height = 2f },
        Position = Vector3.Up,
    };
}
