using System.Collections.Generic;
using Godot;
using UnturnedGodot.Net;
using UnturnedGodot.Player;

namespace UnturnedGodot;

// Renders the other players: one real character body per remote, driven by the interpolated snapshot
// stream (position, yaw, pitch). Moving/idle animation state derives from the sampled motion, mirroring
// how Unturned's remote players animate from their replicated state rather than their inputs.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class RemotePlayersView : Node3D
{
    private sealed class Avatar
    {
        public required Node3D Root;
        public CharacterSkeleton? Rig;
        public Vector3 LastPosition;

        // State-derived movement audio: footsteps/landings computed locally from the REPLICATED stance,
        // moving and grounded flags — never inferred from interpolated motion (stalled interpolation used
        // to fake mid-air touchdowns and double-thud jumps).
        public MovementAudio? Audio;
    }

    private NetClient _client = null!;
    private string _unturnedPath = "";
    private System.Func<MovementAudio>? _audioFactory;
    private readonly Dictionary<byte, Avatar> _avatars = new();

    public static RemotePlayersView Create(NetClient client, string unturnedPath,
        System.Func<MovementAudio>? audioFactory) => new()
        {
            Name = "RemotePlayers",
            _client = client,
            _unturnedPath = unturnedPath,
            _audioFactory = audioFactory,
        };

    public override void _Process(double delta)
    {
        double now = NetworkManager.Now;

        foreach ((byte id, RemotePlayer remote) in _client.Remotes)
        {
            if (!_avatars.TryGetValue(id, out Avatar? avatar))
                _avatars[id] = avatar = Spawn(id, remote.Name);

            PoseSnapshot pose = remote.Sample(now);
            avatar.Root.Position = pose.Position;
            avatar.Root.RotationDegrees = new Vector3(0, pose.Yaw, 0);

            // Animate from the replicated input-derived flag, exactly what the owner's controller uses:
            // position deltas would flicker walk/idle during in-place jumps (vertical motion) and packet
            // stalls, restarting the crossfade mid-air.
            avatar.Rig?.SetState(remote.Stance, remote.Moving);
            avatar.Rig?.SetPitch(pose.Pitch - 90f); // wire pitch (0..180) -> Godot pitch (-90..+90)

            avatar.Audio?.Tick(remote.Stance, remote.Moving, remote.Grounded, pose.Position, (float)delta);
            avatar.LastPosition = pose.Position;
        }

        // Drop avatars for players that left.
        List<byte>? gone = null;
        foreach (byte id in _avatars.Keys)
            if (!_client.Remotes.ContainsKey(id))
                (gone ??= new List<byte>()).Add(id);
        if (gone != null)
            foreach (byte id in gone)
            {
                _avatars[id].Root.QueueFree();
                _avatars.Remove(id);
            }
    }

    private Node3D? _template; // one full CharacterModel.Build; every remote is a cheap Clone of it

    private Avatar Spawn(byte id, string name)
    {
        var root = new Node3D { Name = $"Remote_{id}" };
        MovementAudio? audio = _audioFactory?.Invoke();
        // Building the body re-parses resources.assets (~100 ms and tens of MB of transient heap per
        // remote); the template is built once and cloned per player — identical mesh, skin and clips.
        _template ??= CharacterModel.Build(_unturnedPath);
        Node3D? body = CharacterModel.Clone(_template);
        CharacterSkeleton? rig = body as CharacterSkeleton;
        root.AddChild(body ?? Placeholder());

        var label = new Label3D
        {
            Text = name,
            Position = new Vector3(0, 2.25f, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FontSize = 48,
            PixelSize = 0.004f,
            OutlineSize = 8,
        };
        root.AddChild(label);

        AddChild(root);
        GD.Print($"[net] remote player '{name}' (id {id}) spawned");
        return new Avatar { Root = root, Rig = rig, Audio = audio };
    }

    private static Node3D Placeholder() => new MeshInstance3D
    {
        Mesh = new CapsuleMesh { Radius = 0.4f, Height = 2f },
        Position = Vector3.Up,
    };
}
