using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Net;

namespace UnturnedGodot;

// The Godot-side owner of the multiplayer session. Three shapes, all over the same core stack:
//  - Listen server ("open to LAN"): NetServer over loopback+UDP composite, the host joins via loopback.
//  - Client: NetClient over UDP to someone else's server (JOIN=host:port).
//  - Dedicated: see DedicatedServer (no local player at all).
// Pumps everything on the physics tick and forwards the local player's 12.5 Hz inputs.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class NetworkManager : Node
{
    public const ushort DefaultPort = 27015;

    private NetServer? _server;
    private IServerTransport? _serverTransport;
    private NetClient? _client;
    private IClientTransport? _clientTransport;

    public NetClient? Client => _client;
    public bool IsHosting => _server != null;
    public bool IsActive => _client != null || _server != null;

    private GroundSampler _ground = FlatFallback;
    private Vector3 _spawn;

    private static bool FlatFallback(float x, float z, out float y)
    {
        y = 0f;
        return true;
    }

    public static double Now => Time.GetTicksMsec() / 1000.0;

    public void Configure(HeightmapSampler heights, Vector3 spawn)
    {
        _spawn = spawn;
        _ground = (float x, float z, out float y) => heights.TrySampleHeight(x, -z, out y);
    }

    // Minecraft-style "open to LAN": the running singleplayer becomes a server without a restart.
    public bool StartListenServer(ushort port, string hostName)
    {
        if (IsActive)
            return false;
        UdpServerTransport udp;
        try
        {
            udp = new UdpServerTransport(port);
        }
        catch (System.Net.Sockets.SocketException e)
        {
            GD.PushWarning($"[net] failed to bind UDP port {port}: {e.Message}");
            return false;
        }

        var loopback = new LoopbackServerTransport();
        _serverTransport = new CompositeServerTransport(loopback, udp);
        _server = new NetServer(_serverTransport, new ServerSimulation(new HeightfieldMoveSolver(_ground)), _spawn);

        _clientTransport = loopback.CreateClient();
        _client = new NetClient(_clientTransport, hostName);
        GD.Print($"[net] listen server on UDP {port}; host '{hostName}' joined via loopback");
        return true;
    }

    public void JoinServer(string host, ushort port, string name)
    {
        if (IsActive)
            return;
        _clientTransport = new UdpClientTransport(host, port);
        _client = new NetClient(_clientTransport, name);
        GD.Print($"[net] joining {host}:{port} as '{name}'");
    }

    public override void _PhysicsProcess(double delta)
    {
        double now = Now;
        _server?.Update(now);
        _client?.Update(now);
    }

    public override void _ExitTree()
    {
        _clientTransport?.Close();
        _serverTransport?.Close();
    }
}
