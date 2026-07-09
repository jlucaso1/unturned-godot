using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Net;

namespace UnturnedGodot;

// Standalone dedicated server: `godot --headless -- --server [--port=27015]`. Loads only what the
// authoritative movement sim needs (the map's heightmap tiles) and runs NetServer over raw UDP —
// no rendering, no local player.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class DedicatedServer : Node
{
    private NetServer _server = null!;
    private IServerTransport _transport = null!;

    public static DedicatedServer Create(string unturnedPath, string mapName, Vector3 spawn, ushort port)
    {
        var level = new LevelInfo(System.IO.Path.Combine(unturnedPath, "Maps", mapName));
        var tiles = new List<HeightmapTile>();
        foreach ((int x, int y) in level.EnumerateTiles())
            tiles.Add(HeightmapTile.Read(level.HeightmapPath(x, y), x, y));
        var heights = new HeightmapSampler(tiles);
        GroundSampler ground = (float x, float z, out float y) => heights.TrySampleHeight(x, -z, out y);

        var transport = new UdpServerTransport(port);
        var node = new DedicatedServer
        {
            Name = "DedicatedServer",
            _transport = transport,
            _server = new NetServer(transport, new ServerSimulation(new HeightfieldMoveSolver(ground)), spawn),
        };
        GD.Print($"[server] dedicated server for {mapName} listening on UDP {port} ({tiles.Count} height tiles)");
        return node;
    }

    private double _lastStatus;

    public override void _PhysicsProcess(double delta)
    {
        _server.Update(NetworkManager.Now);
        if (NetworkManager.Now - _lastStatus >= 10.0)
        {
            _lastStatus = NetworkManager.Now;
            GD.Print($"[server] {_server.PlayerCount} player(s) online");
        }
    }

    public override void _ExitTree() => _transport.Close();
}
