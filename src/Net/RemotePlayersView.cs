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

        // State-derived movement audio (footsteps/landings computed locally, never networked) plus the
        // vertical-motion bookkeeping that stands in for a grounded flag we don't replicate: rising or
        // falling fast marks the avatar airborne, and it only lands again after an actual FALL — the
        // near-zero vertical speed at a jump's apex must not read as a landing.
        public MovementAudio? Audio;
        public bool Airborne;
        public bool WasFalling;
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

            if (avatar.Audio != null && delta > 0)
            {
                float dy = (pose.Position.Y - avatar.LastPosition.Y) / (float)delta;
                if (Mathf.Abs(dy) >= 1.5f)
                {
                    avatar.Airborne = true;
                    if (dy < -1.5f)
                        avatar.WasFalling = true;
                }
                else if (avatar.Airborne && avatar.WasFalling)
                {
                    avatar.Airborne = false; // touched down after a real fall -> the clock sees the edge
                    avatar.WasFalling = false;
                }
                // else: a jump's apex (slow but never fell yet) stays airborne; flat ground stays grounded.

                avatar.Audio.Tick(remote.Stance, remote.Moving, !avatar.Airborne, pose.Position, (float)delta);
            }
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

    private Avatar Spawn(byte id, string name)
    {
        var root = new Node3D { Name = $"Remote_{id}" };
        MovementAudio? audio = _audioFactory?.Invoke();
        Node3D? body = CharacterModel.Build(_unturnedPath);
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
