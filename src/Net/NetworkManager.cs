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
    private CompositeServerTransport? _serverTransport;
    private NetClient? _client;
    private IClientTransport? _clientTransport;

    public NetClient? Client => _client;
    public NetServer? Server => _server; // extension seam owner: future systems hook OnTick/Broadcast
    public bool IsHosting => _server != null;
    public bool IsLanOpen { get; private set; }
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

    // The always-on session, Unturned's Provider shape: singleplayer IS a loopback server with the local
    // player as its first client. Every gameplay feature is then written once as server logic +
    // replication and works identically solo, LAN and dedicated.
    public void StartSingleplayer(string hostName)
    {
        if (IsActive)
            return;
        var loopback = new LoopbackServerTransport();
        _serverTransport = new CompositeServerTransport(loopback);
        _server = new NetServer(_serverTransport, new ServerSimulation(new HeightfieldMoveSolver(_ground)), _spawn);
        _clientTransport = loopback.CreateClient();
        _client = new NetClient(_clientTransport, hostName);
        GD.Print($"[net] local session up; '{hostName}' joined via loopback");
    }

    // Minecraft-style "open to LAN": attach a UDP listener to the ALREADY-RUNNING local server.
    public bool OpenToLan(ushort port)
    {
        if (_server == null || _serverTransport == null || IsLanOpen)
            return false;
        try
        {
            _serverTransport.Add(new UdpServerTransport(port));
        }
        catch (System.Net.Sockets.SocketException e)
        {
            GD.PushWarning($"[net] failed to bind UDP port {port}: {e.Message}");
            return false;
        }
        IsLanOpen = true;
        GD.Print($"[net] open to LAN on UDP {port}");
        return true;
    }

    // Brings the level's zombie population up on the hosted server (no-op for pure clients): the
    // ZombieHost hooks the NetServer extension seams, so solo, LAN and dedicated all share it.
    public void HostZombies(string levelDir)
    {
        if (_server == null)
            return;
        UnturnedGodot.Zombies.ZombieSystem? zombies =
            UnturnedGodot.Zombies.ZombieWorld.Load(levelDir, _ground, new System.Random());
        if (zombies == null)
        {
            GD.PushWarning("[zombies] level ships no zombie data; skipping");
            return;
        }
        // AlertTool's BLOCK_VISION raycast against the world's real colliders (terrain, objects,
        // buildings): stealth detection fails when geometry hides the player. Stops at 95% of the
        // distance exactly like the original so the ray never clips the target's own capsule. The
        // ZombieHost ticks inside _PhysicsProcess, so querying the physics space here is safe.
        // Query-parameter objects are engine objects, so both closures reuse one instead of
        // allocating (and later finalizing) a fresh one per zombie step.
        var ray = new PhysicsRayQueryParameters3D();
        zombies.VisionBlocked = (from, to) =>
        {
            PhysicsDirectSpaceState3D? space = GetViewport()?.World3D?.DirectSpaceState;
            if (space == null)
                return false;
            ray.From = from;
            ray.To = from + ((to - from) * 0.95f);
            return space.IntersectRay(ray).Count > 0;
        };
        // The CharacterController's collide-and-slide against real world colliders: sweep a sphere
        // at chest height along the step; when it hits, advance to the safe fraction and slide the
        // remainder along the contact plane (one slide iteration, like a CC's per-frame resolve).
        var sweep = new SphereShape3D();
        var query = new PhysicsShapeQueryParameters3D { Shape = sweep };
        float lastRadius = -1f;
        zombies.MoveResolver = (from, to, radius) =>
        {
            PhysicsDirectSpaceState3D? space = GetViewport()?.World3D?.DirectSpaceState;
            if (space == null)
                return to;
            if (radius != lastRadius) // one capsule size per speciality; skip the redundant engine write
            {
                sweep.Radius = radius;
                lastRadius = radius;
            }
            Vector3 chest = Vector3.Up; // sweep at capsule-middle height, above ground clutter
            Vector3 motion = to - from;
            query.Transform = new Transform3D(Basis.Identity, from + chest);
            query.Motion = motion;
            float[] cast = space.CastMotion(query);
            if (cast[0] >= 1f)
                return to; // clear path
            Vector3 safe = from + (motion * cast[0]);

            // Contact normal at the blocked spot -> slide the remaining motion along the surface.
            query.Transform = new Transform3D(Basis.Identity, safe + chest);
            query.Motion = Vector3.Zero;
            Vector3 normal = space.GetRestInfo(query) is { Count: > 0 } rest
                ? (Vector3)rest["normal"]
                : Vector3.Zero;
            if (normal == Vector3.Zero)
                return safe;
            Vector3 remaining = motion * (1f - cast[0]);
            Vector3 slide = remaining - (normal * remaining.Dot(normal));
            query.Transform = new Transform3D(Basis.Identity, safe + chest);
            query.Motion = slide;
            float[] slideCast = space.CastMotion(query);
            return safe + (slide * slideCast[0]);
        };
        _ = new UnturnedGodot.Zombies.ZombieHost(zombies, _server);
        GD.Print($"[zombies] {zombies.Zombies.Count} zombies spawned from the level's spawnpoints");
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
