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
        public bool Streaming; // any ZombieStates received yet (idle zombies never stream)
    }

    private string _unturnedPath = "";
    private readonly Dictionary<ushort, ZombieAvatar> _avatars = new();
    private Node3D? _normalTemplate;
    private Node3D? _megaTemplate;
    private readonly RandomNumberGenerator _rng = new();

    public static ZombiesView Create(NetClient client, string unturnedPath)
    {
        var view = new ZombiesView { Name = "Zombies", _unturnedPath = unturnedPath };
        client.OnUnhandledMessage += view.Handle;
        return view;
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
            avatar.State = state.State;
            avatar.Rig?.Play(ClipFor(avatar));
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
                PoseSnapshot pose = avatar.Buffer.GetCurrentSnapshot(now);
                avatar.Root.Position = pose.Position;
                avatar.Root.RotationDegrees = new Vector3(0, pose.Yaw, 0);
            }

            // Far zombies keep their pose but stop sampling animation, like Unturned's inactive regions.
            if (avatar.Rig is { } rig && eye is { } camera)
            {
                bool animate = avatar.Root.Position.DistanceSquaredTo(camera) <= AnimateWithin * AnimateWithin;
                rig.ProcessMode = animate ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
            }
        }
    }

    // Zombie.cs's animation selection, driven by the replicated behavior state.
    private string ClipFor(ZombieAvatar avatar) => avatar.State switch
    {
        EZombieState.Chase or EZombieState.Return => MoveClip(avatar),
        EZombieState.Attack => $"Attack_{_rng.RandiRange(0, 4)}", // sendZombieAttack's normal swing range
        _ => IdleClip(avatar),
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

    private static Node3D Placeholder() => new MeshInstance3D
    {
        Mesh = new CapsuleMesh { Radius = 0.4f, Height = 2f },
        Position = Vector3.Up,
    };
}
