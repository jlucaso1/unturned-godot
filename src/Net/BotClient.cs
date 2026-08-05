using Godot;
using UnturnedGodot.Net;

namespace UnturnedGodot;

// Headless scripted client for standalone multiplayer verification: BOT_JOIN=host:port connects a
// NetClient over UDP, walks forward, and periodically prints what it sees (its own server state and
// every remote player's interpolated position). BOT_SECONDS bounds the run (default 20).
//
// It asks the server which level it runs before saying Hello. A real client does that to BUILD the
// right world; this one builds nothing, so it simply adopts the answer — which also makes every bot
// run an end-to-end exercise of the pre-join query over real sockets.
public partial class BotClient : Node
{
    private NetClient? _client;

    // Whether the scripted run got in. The bot reports itself through the log and the exit status, which
    // is right for an unattended check and useless to anything watching it in-process — a test can see a
    // bot that was constructed but not one that was admitted, and those are the two halves of a join.
    internal bool Joined => _client is { Joined: true };
    private ServerQuery _query = null!;
    private string _botName = "Bot";
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
            _botName = name,
            _lifetime = lifetime,
        };
        node._query = new ServerQuery(transport);
        Log.Print($"[bot] '{name}' asking {host}:{port} which level it runs ({lifetime}s run)");
        return node;
    }

    private NetClient StartSession(string level)
    {
        var client = new NetClient(_transport, _botName, level);
        client.OnRejected += rejection =>
            Log.PushWarning($"[bot] refused: {NetworkManager.Describe(rejection)}");
        client.OnUnhandledMessage += payload =>
        {
            // A subscriber that reads the wire owns its decode guard — NetClient's is scoped to its own
            // messages, so a truncated ZombieList reaching here unguarded would end the process.
            if (!MalformedPacket.TryDecode(payload, ReadType, out ENetMessage type))
                return;

            switch (type)
            {
                case ENetMessage.ZombieList:
                    if (MalformedPacket.TryDecode(payload, ReadZombieList, out var list))
                        _zombiesListed += list.Listings.Count;
                    break;
                case ENetMessage.ZombieStates:
                    _zombieStateMessages++;
                    break;
            }
        };
        Log.Print($"[bot] '{_botName}' joining on '{level}'");
        return client;
    }

    public override void _PhysicsProcess(double delta)
    {
        double now = NetworkManager.Now;
        if (_started < 0)
            _started = now;

        // Checked before anything else so BOT_SECONDS bounds the run even if the handshake never
        // completes (no server there, or one that refuses us).
        if (now - _started > _lifetime)
        {
            Log.Print("[bot] done");
            // Through AppShutdown, like every other way out: the bot has nothing running to wait for, but
            // a native SceneTree.Quit never returns to managed code, so a coverage run over the scripted
            // client recorded zero lines however far it got.
            AppShutdown.QuitNow(GetTree());
            return;
        }

        // Nothing to say until we know which world this server runs.
        if (_client == null)
        {
            _query.Update(now);
            if (_query.State == EServerQueryState.TimedOut)
            {
                Log.PrintErr("[bot] no server answered the level query; giving up");
                // The same door the lifetime exit takes. This is the one a coverage run actually
                // reaches — a bot pointed at a port with no listener times out long before its lifetime
                // expires — and a native quit here records no hit counts at all.
                AppShutdown.QuitNow(GetTree(), 1);
                return;
            }
            if (_query.State != EServerQueryState.Answered)
                return;
            _client = StartSession(_query.Info!.Level);
        }

        NetClient client = _client;
        client.Update(now);

        if (client.Joined && now - _lastInput >= UnturnedGodot.Net.ServerSimulation.TickRate)
        {
            _lastInput = now;
            client.SendInput(new InputCommand(_frame++, 0, -1, jump: false, sprint: false,
                yaw: NetAngles.QuantizeYaw(0f), pitch: 90));
        }

        if (now - _lastReport >= 1.0)
        {
            _lastReport = now;
            var line = new System.Text.StringBuilder();
            line.Append($"[bot] joined={client.Joined} id={client.PlayerId} " +
                $"self=({client.LocalServerState.Position.X:F1},{client.LocalServerState.Position.Y:F1},{client.LocalServerState.Position.Z:F1})");
            foreach ((byte id, RemotePlayer remote) in client.Remotes)
            {
                PoseSnapshot pose = remote.Sample(now);
                line.Append($" | {remote.Name}#{id}=({pose.Position.X:F1},{pose.Position.Y:F1},{pose.Position.Z:F1})");
            }
            line.Append($" | zombies={_zombiesListed} zombieStateMsgs={_zombieStateMessages}");
            Log.Print(line.ToString());
        }
    }

    public override void _ExitTree() => _transport.Close();
}
