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

    // levelName is what the handshake compares (the map's folder name), which is not always mapName:
    // a workshop selection key carries an absolute path that means nothing on the joining machine.
    public static DedicatedServer Create(string unturnedPath, string mapName, string levelName, Vector3 spawn,
        ushort port)
    {
        // Through the catalog, not Maps/<name>: a workshop map lives in the workshop content folder, and
        // hard-coding the shipped location left the server advertising that map while loading no terrain
        // and no zombies from it. The client's spawn resolution already goes through the same lookup.
        var level = new LevelInfo(MapCatalog.ResolvePath(unturnedPath, mapName));
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
            _server = new NetServer(transport, new ServerSimulation(new HeightfieldMoveSolver(ground)), spawn,
                levelName),
        };

        UnturnedGodot.Zombies.ZombieSystem? zombies = UnturnedGodot.Zombies.ZombieWorld.Load(
            level.Path, ground, new System.Random());
        if (zombies != null)
        {
            node._zombieNavigation = ZombieNavigation.Build(zombies.Navmesh);
            if (node._zombieNavigation != null)
            {
                zombies.PathQuery = node._zombieNavigation.Query;
                zombies.PathReady = () => node._zombieNavigation?.IsReady == true;
            }
            _ = new UnturnedGodot.Zombies.ZombieHost(zombies, node._server);
            Log.Print($"[server] {zombies.Zombies.Count} zombies spawned");
        }

        Log.Print($"[server] dedicated server for {levelName} listening on UDP {port} ({tiles.Count} height tiles)");
        return node;
    }

    private double _lastStatus;

    public override void _PhysicsProcess(double delta)
    {
        _server.Update(NetworkManager.Now);
        if (NetworkManager.Now - _lastStatus >= 10.0)
        {
            _lastStatus = NetworkManager.Now;
            Log.Print($"[server] {_server.PlayerCount} player(s) online");
        }
    }

    private ZombieNavigation? _zombieNavigation;

    public override void _ExitTree()
    {
        _transport.Close();
        _zombieNavigation?.Free();
    }
}
