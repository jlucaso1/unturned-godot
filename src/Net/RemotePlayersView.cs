using System.Collections.Generic;
using Godot;
using UnturnedGodot.Net;
using UnturnedGodot.Player;

namespace UnturnedGodot;

// Renders the other players: one real character body per remote, driven by the interpolated snapshot
// stream (position, yaw, pitch). Moving/idle animation state derives from the sampled motion, mirroring
// how Unturned's remote players animate from their replicated state rather than their inputs.
public partial class RemotePlayersView : Node3D
{
    private sealed class Avatar
    {
        public required Node3D Root;
        public CharacterSkeleton? Rig;
        public Vector3 LastPosition;

        // Mirrors of the last values submitted to the scene tree. Remote snapshots often hold a
        // stationary player for many render frames; avoid crossing the Godot boundary and rebuilding
        // the animation-state string when the value is unchanged. This is the same dirty-state pattern
        // Veloren uses for replicated/rendered state, and is presentation-only: sampling, interpolation
        // and movement audio still run every frame.
        public bool PoseApplied;
        public float LastYaw;
        public bool StateApplied;
        public EPlayerStance LastStance;
        public bool LastMoving;
        public bool PitchApplied;
        public float LastPitch;

        // State-derived movement audio: footsteps/landings computed locally from the REPLICATED stance,
        // moving and grounded flags — never inferred from interpolated motion (stalled interpolation used
        // to fake mid-air touchdowns and double-thud jumps).
        public MovementAudio? Audio;
    }

    private NetClient _client = null!;
    private string _unturnedPath = "";
    private System.Func<MovementAudio>? _audioFactory;

    // The shared positional voice pool, for the sounds a gesture makes. Separate from the movement audio
    // above because a swing is not derived from replicated state the way a footstep is — it is a
    // consequence of a replicated EVENT, played once where it happened.
    private OneShotAudio? _oneShot;
    private readonly Dictionary<byte, Avatar> _avatars = new();
    private bool _ownsTemplate;

    public static RemotePlayersView Create(NetClient client, string unturnedPath,
        System.Func<MovementAudio>? audioFactory, Node3D? localTemplate = null,
        OneShotAudio? oneShot = null) => new()
        {
            Name = "RemotePlayers",
            _client = client,
            _unturnedPath = unturnedPath,
            _audioFactory = audioFactory,
            _oneShot = oneShot,
            _template = localTemplate,
            _ownsTemplate = localTemplate == null,
        };

    public override void _ExitTree()
    {
        if (_ownsTemplate)
            _template?.Free();
        _template = null;
    }

    public override void _Process(double delta)
    {
        double now = NetworkManager.Now;

        foreach ((byte id, RemotePlayer remote) in _client.Remotes)
        {
            if (!_avatars.TryGetValue(id, out Avatar? avatar))
                _avatars[id] = avatar = Spawn(id, remote.Name);

            PoseSnapshot pose = remote.Sample(now);
            if (!avatar.PoseApplied || avatar.LastPosition != pose.Position)
            {
                avatar.Root.Position = pose.Position;
                avatar.LastPosition = pose.Position;
            }
            if (!avatar.PoseApplied || avatar.LastYaw != pose.Yaw)
                avatar.Root.RotationDegrees = new Vector3(0, pose.Yaw, 0);
            avatar.PoseApplied = true;
            avatar.LastYaw = pose.Yaw;

            // Animate from the replicated input-derived flag, exactly what the owner's controller uses:
            // position deltas would flicker walk/idle during in-place jumps (vertical motion) and packet
            // stalls, restarting the crossfade mid-air.
            if (!avatar.StateApplied || avatar.LastStance != remote.Stance || avatar.LastMoving != remote.Moving)
            {
                avatar.Rig?.SetState(remote.Stance, remote.Moving);
                avatar.StateApplied = true;
                avatar.LastStance = remote.Stance;
                avatar.LastMoving = remote.Moving;
            }
            float pitch = pose.Pitch - 90f;
            if (!avatar.PitchApplied || avatar.LastPitch != pitch)
            {
                avatar.Rig?.SetPitch(pitch); // wire pitch (0..180) -> Godot pitch (-90..+90)
                avatar.PitchApplied = true;
                avatar.LastPitch = pitch;
            }

            // One-shot hand animations the server said this player performed. Taken (not peeked) so each
            // swing plays exactly once, and applied after the movement state above so it wins the frame
            // it arrives on — SetState defers to a gesture that already owns the rig.
            if (remote.PendingGesture != EPlayerGesture.None)
            {
                EPlayerGesture gesture = remote.TakeGesture();
                if (PlayerGestures.ClipFor(gesture) is { } clip)
                    avatar.Rig?.PlayOnce(clip);
                // Chest height, like the zombie voices: the swing comes from the body, not its feet.
                if (PlayerGestures.SoundFor(gesture) is { } sound)
                    _oneShot?.Play(sound, pose.Position + Vector3.Up,
                        PlayerGestures.PunchVolume, PlayerGestures.PunchMaxDistance);
            }

            avatar.Audio?.Tick(remote.Stance, remote.Moving, remote.Grounded, pose.Position, (float)delta);
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

    // Main passes the already-imported local Player_Client. Joining multiplayer therefore never repeats
    // resources.assets parsing on the first remote avatar; the lazy build remains for standalone callers.
    private Node3D? _template;

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
        Log.Print($"[net] remote player '{name}' (id {id}) spawned");
        return new Avatar { Root = root, Rig = rig, Audio = audio };
    }

    private static Node3D Placeholder() => new MeshInstance3D
    {
        Mesh = new CapsuleMesh { Radius = 0.4f, Height = 2f },
        Position = Vector3.Up,
    };
}
