using Godot;
using UnturnedGodot.Net;

namespace UnturnedGodot;

// Headless scripted client for standalone multiplayer verification: BOT_JOIN=host:port connects a
// NetClient over UDP, walks forward, and periodically prints what it sees (its own server state and
// every remote player's interpolated position). BOT_SECONDS bounds the run (default 20).
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class BotClient : Node
{
    private NetClient _client = null!;
    private IClientTransport _transport = null!;
    private double _started = -1;
    private double _lastReport;
    private double _lastInput;
    private uint _frame;
    private float _lifetime = 20f;
    private int _zombiesListed;
    private int _zombieStateMessages;

    // Cached so a method group does not allocate a delegate on every received message.
    private static readonly System.Func<byte[], ENetMessage> ReadType = NetMessages.TypeOf;
    private static readonly System.Func<byte[],
        (byte Bound, System.Collections.Generic.List<UnturnedGodot.Zombies.ZombieListing> Listings)> ReadZombieList =
        UnturnedGodot.Zombies.ZombieNetMessages.ReadZombieList;

    public static BotClient Create(string host, ushort port, string name, float lifetime)
    {
        var transport = new UdpClientTransport(host, port);
        var node = new BotClient
        {
            Name = "BotClient",
            _transport = transport,
            _client = new NetClient(transport, name),
            _lifetime = lifetime,
        };
        node._client.OnUnhandledMessage += payload =>
        {
            // A subscriber that reads the wire owns its decode guard — NetClient's is scoped to its own
            // messages, so a truncated ZombieList reaching here unguarded would end the process.
            if (!MalformedPacket.TryDecode(payload, ReadType, out ENetMessage type))
                return;

            switch (type)
            {
                case ENetMessage.ZombieList:
                    if (MalformedPacket.TryDecode(payload, ReadZombieList, out var list))
                        node._zombiesListed += list.Listings.Count;
                    break;
                case ENetMessage.ZombieStates:
                    node._zombieStateMessages++;
                    break;
            }
        };
        Log.Print($"[bot] '{name}' connecting to {host}:{port} for {lifetime}s");
        return node;
    }

    public override void _PhysicsProcess(double delta)
    {
        double now = NetworkManager.Now;
        if (_started < 0)
            _started = now;

        _client.Update(now);

        if (_client.Joined && now - _lastInput >= UnturnedGodot.Net.ServerSimulation.TickRate)
        {
            _lastInput = now;
            _client.SendInput(new InputCommand(_frame++, 0, -1, jump: false, sprint: false,
                yaw: NetAngles.QuantizeYaw(0f), pitch: 90));
        }

        if (now - _lastReport >= 1.0)
        {
            _lastReport = now;
            var line = new System.Text.StringBuilder();
            line.Append($"[bot] joined={_client.Joined} id={_client.PlayerId} " +
                $"self=({_client.LocalServerState.Position.X:F1},{_client.LocalServerState.Position.Y:F1},{_client.LocalServerState.Position.Z:F1})");
            foreach ((byte id, RemotePlayer remote) in _client.Remotes)
            {
                PoseSnapshot pose = remote.Sample(now);
                line.Append($" | {remote.Name}#{id}=({pose.Position.X:F1},{pose.Position.Y:F1},{pose.Position.Z:F1})");
            }
            line.Append($" | zombies={_zombiesListed} zombieStateMsgs={_zombieStateMessages}");
            Log.Print(line.ToString());
        }

        if (now - _started > _lifetime)
        {
            Log.Print("[bot] done");
            GetTree().Quit();
        }
    }

    public override void _ExitTree() => _transport.Close();
}
