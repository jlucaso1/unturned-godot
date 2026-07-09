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
            switch (NetMessages.TypeOf(payload))
            {
                case ENetMessage.ZombieList:
                    node._zombiesListed += UnturnedGodot.Zombies.ZombieNetMessages.ReadZombieList(payload).Listings.Count;
                    break;
                case ENetMessage.ZombieStates:
                    node._zombieStateMessages++;
                    break;
            }
        };
        GD.Print($"[bot] '{name}' connecting to {host}:{port} for {lifetime}s");
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
            GD.Print(line.ToString());
        }

        if (now - _started > _lifetime)
        {
            GD.Print("[bot] done");
            GetTree().Quit();
        }
    }

    public override void _ExitTree() => _transport.Close();
}
