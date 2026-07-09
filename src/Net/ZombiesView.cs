using System.Collections.Generic;
using Godot;
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
    private const float LargeDistance = 16f;   // tellState's interpolation-reset threshold
    private const float AnimateWithin = 100f;  // skeletons past this stop sampling (Unturned's far
                                               // zombie regions deactivate entirely)

    private sealed class ZombieAvatar
    {
        public required Node3D Root;
        public CharacterSkeleton? Rig;
        public readonly SnapshotBuffer Buffer = new();
        public Vector3 LastUpdatePos;
        public EZombieSpeciality Speciality;
        public byte Move;
        public byte Idle;
        public EZombieState State;
        public bool Streaming;     // any ZombieStates received yet (idle zombies never stream)
        public double StartleUntil; // while set, the wake-up roar plays before the state clip
        public double NextGroan;    // Zombie.cs's groan loop clock

        // C#-side mirrors of engine state, so the per-frame loop never crosses into Godot for a value it
        // already knows, and never writes one that hasn't changed. With the whole population asleep the
        // loop makes zero interop calls; the values written are exactly the ones written before.
        public Vector3 KnownPosition;  // Root.Position as last written
        public float AppliedYaw;       // Root.RotationDegrees.Y as last written
        public bool AnimationActive = true; // rig.ProcessMode as last written (Inherit on spawn)
    }

    private string _unturnedPath = "";
    private OneShotAudio? _audio;
    private readonly Dictionary<ushort, ZombieAvatar> _avatars = new();
    private Node3D? _normalTemplate;
    private Node3D? _megaTemplate;
    private readonly RandomNumberGenerator _rng = new();

    public static ZombiesView Create(NetClient client, string unturnedPath, OneShotAudio? audio)
    {
        var view = new ZombiesView { Name = "Zombies", _unturnedPath = unturnedPath, _audio = audio };
        client.OnUnhandledMessage += view.Handle;
        return view;
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
        switch (NetMessages.TypeOf(payload))
        {
            case ENetMessage.ZombieList:
                foreach (ZombieListing listing in ZombieNetMessages.ReadZombieList(payload))
                    SpawnOrReset(listing);
                break;
            case ENetMessage.ZombieStates:
                double now = NetworkManager.Now;
                foreach (ZombieSnapshotState state in ZombieNetMessages.ReadZombieStates(payload).States)
                    Push(state, now);
                break;
        }
    }

    private void SpawnOrReset(in ZombieListing listing)
    {
        if (!_avatars.TryGetValue(listing.Id, out ZombieAvatar? avatar))
            _avatars[listing.Id] = avatar = Spawn(listing);

        avatar.Speciality = listing.Speciality;
        avatar.Move = listing.Move;
        avatar.Idle = listing.Idle;
        avatar.Root.Position = listing.Position;
        avatar.Root.RotationDegrees = new Vector3(0, NetAngles.DequantizeYaw(listing.Yaw), 0);
        avatar.KnownPosition = listing.Position;
        avatar.AppliedYaw = NetAngles.DequantizeYaw(listing.Yaw);
        avatar.LastUpdatePos = listing.Position;
        avatar.Buffer.UpdateLastSnapshot(
            new PoseSnapshot(listing.Position, 0f, NetAngles.DequantizeYaw(listing.Yaw)), NetworkManager.Now);
        avatar.Rig?.Play(IdleClip(avatar));
    }

    private ZombieAvatar Spawn(in ZombieListing listing)
    {
        bool isMega = listing.Speciality == EZombieSpeciality.Mega;
        Node3D? template = Template(isMega);
        Node3D body = CharacterModel.Clone(template) ?? Placeholder();

        var root = new Node3D { Name = $"Zombie_{listing.Id}" };
        // Zombie.cs randomizes each zombie's visual scale locally on spawn.
        root.Scale = Vector3.One * (isMega ? _rng.RandfRange(1.45f, 1.55f) : _rng.RandfRange(0.95f, 1.05f));
        root.AddChild(body);
        AddChild(root);

        return new ZombieAvatar { Root = root, Rig = body as CharacterSkeleton };
    }

    private Node3D? Template(bool isMega)
    {
        if (isMega)
            return _megaTemplate ??= CharacterModel.BuildZombie(_unturnedPath, isMega: true);
        return _normalTemplate ??= CharacterModel.BuildZombie(_unturnedPath, isMega: false);
    }

    private void Push(in ZombieSnapshotState state, double now)
    {
        if (!_avatars.TryGetValue(state.Id, out ZombieAvatar? avatar))
            return; // its ZombieList chunk hasn't arrived yet; the reliable channel will deliver it

        var pose = new PoseSnapshot(state.Position, 0f, NetAngles.DequantizeYaw(state.Yaw));
        bool largeDelta = (state.Position - avatar.LastUpdatePos).LengthSquared() > LargeDistance * LargeDistance;
        avatar.LastUpdatePos = state.Position;
        if (largeDelta)
            avatar.Buffer.UpdateLastSnapshot(pose, now);
        else
            avatar.Buffer.AddNewSnapshot(pose, now);
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
                avatar.Rig?.Play(ClipFor(avatar));
                if (avatar.State == EZombieState.Attack)
                    PlayVoice(avatar, "ZombieRoars"); // askAttack's swing roar
            }
        }
    }

    public override void _Process(double delta)
    {
        double now = NetworkManager.Now;
        Vector3? eye = GetViewport()?.GetCamera3D()?.GlobalPosition;

        foreach (ZombieAvatar avatar in _avatars.Values)
        {
            if (avatar.Streaming)
            {
                // Write the interpolated pose only when it differs from what the node already holds —
                // once a zombie settles back to sleep the buffer keeps returning the same pose, and
                // re-writing it every frame is pure interop cost for zero visual change.
                PoseSnapshot pose = avatar.Buffer.GetCurrentSnapshot(now);
                if (pose.Position != avatar.KnownPosition)
                {
                    avatar.Root.Position = pose.Position;
                    avatar.KnownPosition = pose.Position;
                }
                if (pose.Yaw != avatar.AppliedYaw)
                {
                    avatar.Root.RotationDegrees = new Vector3(0, pose.Yaw, 0);
                    avatar.AppliedYaw = pose.Yaw;
                }
            }

            if (avatar.StartleUntil > 0 && now >= avatar.StartleUntil)
            {
                avatar.StartleUntil = 0;
                avatar.Rig?.Play(ClipFor(avatar)); // the roar ended: pick up the current state clip
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
    }

    // Zombie.cs's animation selection, driven by the replicated behavior state.
    private string ClipFor(ZombieAvatar avatar) => avatar.State switch
    {
        EZombieState.Chase or EZombieState.Return => MoveClip(avatar),
        EZombieState.Attack => AttackClip(avatar),
        _ => IdleClip(avatar),
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

    private static Node3D Placeholder() => new MeshInstance3D
    {
        Mesh = new CapsuleMesh { Radius = 0.4f, Height = 2f },
        Position = Vector3.Up,
    };
}
