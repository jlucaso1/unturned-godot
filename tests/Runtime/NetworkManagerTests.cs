using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Net;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The session, from the outside.
//
// The arrangement worth understanding is that there is always one. Singleplayer is not a special case —
// it is the same authoritative server the network path uses, reached over loopback — which is why "open
// to LAN" can be a listener bolted onto a session already running rather than a restart into another
// mode. That is Minecraft's trick, and it only works if a solo session is a real server all along.
//
// What follows from it is what these hold: a session cannot be started twice, LAN cannot be opened on
// something that is not hosting, and the reasons a join is refused have to be sayable in words a player
// can act on.
public class NetworkManagerTests : TestClass
{
    public NetworkManagerTests(Node testScene) : base(testScene) { }

    // Nothing is running until something starts it, and the flags say so distinctly: hosting, active and
    // LAN-open are three different questions.
    [Test]
    public async Task AFreshManagerIsRunningNothing()
    {
        var net = new NetworkManager { LevelName = "PEI" };
        TestScene.AddChild(net);
        await NextFrame();

        Assert.False(net.IsActive);
        Assert.False(net.IsHosting);
        Assert.False(net.IsLanOpen);

        net.QueueFree();
    }

    // A solo session IS a server, reached over loopback. Everything else in the port depends on that
    // being true rather than on singleplayer having its own path.
    [Test]
    public async Task ASoloSessionIsAServerReachedOverLoopback()
    {
        var net = new NetworkManager { LevelName = "PEI" };
        TestScene.AddChild(net);
        net.Configure(new HeightmapSampler(System.Array.Empty<HeightmapTile>()), Vector3.Zero);

        net.StartSingleplayer("Player");
        await NextFrame();

        Assert.True(net.IsActive);
        Assert.True(net.IsHosting, "a solo session is not a server, so nothing can join it later");
        Assert.False(net.IsLanOpen);

        net.QueueFree();
    }

    // Starting twice is refused rather than replacing the session underneath whoever is in it. The menu
    // can send it — a double click on Play — and it must not tear down a running world.
    [Test]
    public async Task ASessionCannotBeStartedTwice()
    {
        var net = new NetworkManager { LevelName = "PEI" };
        TestScene.AddChild(net);
        net.Configure(new HeightmapSampler(System.Array.Empty<HeightmapTile>()), Vector3.Zero);

        net.StartSingleplayer("Player");
        net.StartSingleplayer("Player");   // a second Play click
        await NextFrame();

        Assert.True(net.IsActive);
        net.QueueFree();
    }

    // LAN cannot be opened on a session that is not hosting: there is no server to attach a listener to,
    // and answering true would tell the player a port is open when nothing is listening.
    [Test]
    public async Task LanCannotBeOpenedWithoutAHost()
    {
        var net = new NetworkManager { LevelName = "PEI" };
        TestScene.AddChild(net);
        await NextFrame();

        Assert.False(net.OpenToLan(42995));
        Assert.False(net.IsLanOpen);

        net.QueueFree();
    }

    // And opening it twice is refused too — the listener is already there, and binding a second would
    // fail on the port rather than doing anything useful.
    [Test]
    public async Task LanIsOnlyOpenedOnce()
    {
        var net = new NetworkManager { LevelName = "PEI" };
        TestScene.AddChild(net);
        net.Configure(new HeightmapSampler(System.Array.Empty<HeightmapTile>()), Vector3.Zero);
        net.StartSingleplayer("Player");
        await NextFrame();

        if (!net.OpenToLan(42994))
        {
            // The port was unavailable on this machine, which is a real answer rather than a failure.
            net.QueueFree();
            return;
        }

        Assert.True(net.IsLanOpen);
        Assert.False(net.OpenToLan(42993), "a second open reported success");

        net.QueueFree();
    }

    // Every refusal has to be sayable. A player who is bounced needs to know whether to pick another
    // map, update the game, or wait for a slot — "connection failed" tells them none of those.
    [Test]
    public void EveryRefusalIsExplainedInWordsAPlayerCanActOn()
    {
        string mismatch = NetworkManager.Describe(
            new JoinRejection(EJoinRejection.LevelMismatch, NetMessages.ProtocolVersion, "Washington"));
        string protocol = NetworkManager.Describe(
            new JoinRejection(EJoinRejection.ProtocolMismatch, 99, "PEI"));
        string full = NetworkManager.Describe(
            new JoinRejection(EJoinRejection.ServerFull, NetMessages.ProtocolVersion, "PEI"));
        string unknown = NetworkManager.Describe(default);

        // The level mismatch names the map, because "wrong map" without saying which is not actionable.
        Assert.Contains("Washington", mismatch);
        // The protocol mismatch names both versions, so a player can tell who is behind.
        Assert.Contains("99", protocol);
        Assert.Contains(NetMessages.ProtocolVersion.ToString(), protocol);
        Assert.Contains("full", full);
        Assert.NotEmpty(unknown); // even an unrecognised reason says something
    }

    // --- a session that runs -------------------------------------------------------------------------

    // A solo session ticks its server and its client every physics frame, over loopback, and keeps
    // running. This is the loop the whole port sits on: if it stopped, nothing in the world would move
    // and nothing would say why.
    [Test]
    public async Task ASoloSessionKeepsTicking()
    {
        using var session = new Session(TestScene);
        session.Net.StartSingleplayer("Player");

        for (int i = 0; i < 30; i++)
            await session.PhysicsFrame();

        Assert.True(session.Net.IsActive);
        Assert.NotNull(session.Net.Client);
        Assert.NotNull(session.Net.Server);

        // The client is ADMITTED, which is the only part of this the frame loop is responsible for.
        // IsActive and the two fields are all assigned synchronously by StartSingleplayer, so without
        // this a manager that stopped pumping Update entirely would still pass.
        Assert.True(session.Net.Client!.Joined,
            "thirty physics frames went by and the loopback client never joined, so nothing is pumping");
    }

    // Hosting zombies on a level that ships none is a warning, not a failure — plenty of maps have no
    // zombie data — and the PUNCH host is deliberately not skipped with them. A level with no population
    // still has trees and rubble standing on it and a player whose fists work, and the streamer hands
    // its damage ledger to that host later regardless.
    [Test]
    public async Task ALevelWithNoZombiesStillGetsAPunchHost()
    {
        using var session = new Session(TestScene);
        session.Net.StartSingleplayer("Player");
        await session.PhysicsFrame();

        session.Net.HostZombies("/nonexistent-map");

        Assert.NotNull(session.Net.PunchDamage);
    }

    // Hosting zombies on a session that is not hosting does nothing at all. A client that joined someone
    // else's server takes this path, and a population spawned there would be a second, invisible world
    // running against the real one.
    //
    // The session is a real CLIENT rather than an idle manager, which is the distinction that matters: a
    // regression gating on IsActive instead of on the server would skip an inactive manager and still
    // host zombies on a joined client, and an idle-manager fixture cannot tell those apart.
    [Test]
    public async Task AClientDoesNotHostItsOwnZombies()
    {
        using var session = new Session(TestScene);
        session.Net.JoinServer("127.0.0.1", RealData.FreePort(), "Player");
        await session.PhysicsFrame();

        Assert.True(session.Net.IsActive, "the fixture is not a client at all");
        Assert.False(session.Net.IsHosting);

        session.Net.HostZombies("/nonexistent-map");

        Assert.Null(session.Net.PunchDamage);
    }

    // A session with no navmesh releases the collision geometry instead of holding it. That builder is
    // the whole map's geometry, recorded during the load for one reconciliation pass — on a session that
    // will never run one (a joined client, a map with no navmesh) it would otherwise sit in memory for
    // the rest of the game with no consumer at all.
    [Test]
    public async Task ASessionThatWillNeverReconcileReleasesTheGeometry()
    {
        using var session = new Session(TestScene);
        session.Net.StartSingleplayer("Player");
        await session.PhysicsFrame();

        var collision = new Data.CollisionFieldBuilder();
        int shape = collision.AddBoxShape(new Vector3(1f, 1f, 1f));
        collision.AddInstance(shape, Transform3D.Identity);
        Assert.True(collision.ShapeCount > 0 && collision.InstanceCount > 0);

        session.Net.ReconcileNavigation(new HashSet<System.Guid>(), collision);
        await session.PhysicsFrame();

        // Released, which is the whole point: an empty builder would make this indistinguishable from
        // doing nothing, because every count is already zero before the call.
        Assert.Equal(0, collision.ShapeCount);
        Assert.Equal(0, collision.InstanceCount);
    }

    // Joining is refused while a session is already running, rather than leaving the player in two at
    // once. The menu can send it: a click on a server in the browser while a solo world is up.
    [Test]
    public async Task JoiningIsRefusedWhileASessionIsRunning()
    {
        using var session = new Session(TestScene);
        session.Net.StartSingleplayer("Player");
        await session.PhysicsFrame();

        NetServer? beforeServer = session.Net.Server;
        NetClient? beforeClient = session.Net.Client;
        session.Net.JoinServer("127.0.0.1", RealData.FreePort(), "Player");

        // Both halves: replacing the CLIENT while leaving the server standing would have the local host
        // player trying to join somebody else's world, and a server-only check cannot see that.
        Assert.Same(beforeServer, session.Net.Server);
        Assert.Same(beforeClient, session.Net.Client);
    }

    // And a join to nothing comes up as a client that is simply never answered. There is no server on
    // that port; the session has to sit there waiting rather than refusing to start, because that is
    // indistinguishable from a host that is slow.
    [Test]
    public async Task AJoinToNothingWaitsRatherThanRefusing()
    {
        using var session = new Session(TestScene);
        session.Net.JoinServer("127.0.0.1", RealData.FreePort(), "Player");

        for (int i = 0; i < 10; i++)
            await session.PhysicsFrame();

        Assert.True(session.Net.IsActive);
        Assert.False(session.Net.IsHosting);

        // And nobody answered, which is the case being sampled. Without this the test would pass just as
        // well against a port something else happens to be listening on.
        Assert.False(session.Net.Client!.Joined, "something answered on a port the OS said was free");
    }

    // --- the real map ---------------------------------------------------------------------------------

    // Hosting the real map's zombies: the population loads, the physics queries attach, and the baked
    // navmesh becomes the router they path with.
    //
    // This is the path a solo session takes on every start, and almost none of it is reachable without
    // real content — the zombie tables, the spawnpoints and the navmesh all come off disk together, and a
    // fixture that supplied one without the others would exercise a shape the game never has.
    [Test]
    public async Task HostingTheRealMapsZombiesBringsUpAPopulation()
    {
        if (!RealMap(out string levelDir))
            return;

        using var session = new Session(TestScene);
        session.Net.StartSingleplayer("Player");
        await session.PhysicsFrame();

        session.Net.HostZombies(levelDir);

        // The punch host is hooked whether or not the level had zombies; that it exists here says the
        // hosting path ran to the end rather than returning early.
        Assert.NotNull(session.Net.PunchDamage);

        for (int i = 0; i < 20; i++)
            await session.PhysicsFrame();

        Assert.True(session.Net.IsActive);
    }

    // The moon reaches the horde AFTER it was spawned. Zombie.isHyper is a live property and
    // LightingManager broadcasts onMoonUpdated on the edge, so a session that started in daylight still
    // gets a hyper population when the full moon comes up — and since nothing in this port respawns a
    // zombie, a value stamped at spawn was the only value any zombie would ever have.
    [Test]
    public async Task TheMoonRisingReachesAPopulationThatIsAlreadyStanding()
    {
        if (!RealMap(out string levelDir))
            return;

        // The map's real cycle, with the phase pinned to the full moon. PEI is saved at 0.32 against a
        // bias of 0.64, so it starts in broad daylight and no moon is up yet.
        using var moonPhase = new EnvFlagScope("MOON_PHASE", LightingCycle.FullMoonPhase.ToString());
        LevelLighting? lighting = LevelLighting.Load(
            System.IO.Path.Combine(levelDir, "Environment", "Lighting.dat"));
        Assert.NotNull(lighting);

        using var session = new Session(TestScene);
        session.Net.StartSingleplayer("Player");
        await session.PhysicsFrame();

        DayNightController cycle = DayNightController.Build(lighting, waterMaterial: null);
        TestScene.AddChild(cycle);
        try
        {
            Assert.True(cycle.IsDaytime);
            Assert.False(cycle.IsFullMoon);

            session.Net.HostZombies(levelDir, dayNight: cycle);
            await session.PhysicsFrame();

            Assert.NotNull(session.Net.Zombies);
            Assert.NotEmpty(session.Net.Zombies!.Zombies);
            Assert.All(session.Net.Zombies.Zombies, z => Assert.False(z.IsHyper));

            // Night falls on a population that is already standing there.
            cycle.TimeOfDay = 0.9f;
            Assert.True(cycle.IsFullMoon, "the fixture did not actually produce a full moon");

            await session.PhysicsFrame();
            Assert.All(session.Net.Zombies.Zombies, z => Assert.True(z.IsHyper));
            Assert.True(session.Net.Zombies.IsNighttime);
        }
        finally
        {
            cycle.QueueFree();
        }
    }

    // And reconciling that map's navmesh against the world runs rather than being skipped. The pass is
    // what makes the baked graph agree with the objects standing on it; a session that skipped it would
    // route zombies through every building on the map.
    [Test]
    public async Task ReconcilingTheRealMapsNavmeshRuns()
    {
        if (!RealMap(out string levelDir))
            return;

        using var session = new Session(TestScene);
        session.Net.StartSingleplayer("Player");
        await session.PhysicsFrame();
        session.Net.HostZombies(levelDir);
        await session.PhysicsFrame();

        session.Net.ReconcileNavigation(new HashSet<System.Guid>());

        // It runs across frames on the physics thread; what matters here is that asking for it neither
        // throws nor leaves the session wedged, since it is started from a signal that fires mid-load.
        for (int i = 0; i < 30; i++)
            await session.PhysicsFrame();

        Assert.True(session.Net.IsActive);
    }

    // PATH_PROBE logs the navmesh route between two points, which is the exact tool for "which opening
    // does the zombie leave the house through". It exists because that question cannot be answered by
    // watching: a zombie taking a strange route looks identical to one taking the only route there is.
    //
    // It retries for fifteen seconds rather than answering once, and that is the part worth keeping: the
    // graph is not ready when a session starts — reconciliation publishes it after the world streams in —
    // so a probe that answered immediately would report "no route" on every map, every time, and mean
    // nothing.
    [Test]
    public async Task ThePathProbeWaitsForAGraphRatherThanAnsweringImmediately()
    {
        if (!RealMap(out string levelDir))
            return;

        // Coordinates nothing else uses, because Log.Tail is a fixed-size RING: in a full suite run the
        // lines between a mark and a check scroll out of it, so a positional slice proves nothing. A
        // string only this test can produce survives that.
        using var probe = new EnvFlagScope("PATH_PROBE", "11,0,11>37,0,37");
        using var session = new Session(TestScene);
        session.Net.StartSingleplayer("Player");
        await session.PhysicsFrame();

        session.Net.HostZombies(levelDir);

        // Long enough for the probe's first timer to fire and for at least one retry to be scheduled.
        int before = Log.Tail().Count;
        for (int i = 0; i < 150; i++)
            await session.PhysicsFrame();

        // The probe REPORTED, on the endpoints only this test asks for. IsActive is set synchronously by
        // StartSingleplayer and nothing here clears it, so ending on that flag was a test that could only
        // fail by crashing — while the thing being covered is a diagnostic that answers, retries until
        // the graph exists, and says what it found.
        Assert.Contains(Log.Tail(), line => line.Contains("(11, 0, 11)", StringComparison.Ordinal));
    }

    // A probe on a map with no navmesh is simply never armed. The flag is a diagnostic, and a diagnostic
    // that crashed a session on a map it could not measure would be worse than no diagnostic.
    [Test]
    public async Task ThePathProbeIsNotArmedWithoutANavmesh()
    {
        // Coordinates nothing else uses, because Log.Tail is a fixed-size RING: in a full suite run the
        // lines between a mark and a check scroll out of it, so a positional slice proves nothing. A
        // string only this test can produce survives that.
        using var probe = new EnvFlagScope("PATH_PROBE", "11,0,11>37,0,37");
        using var session = new Session(TestScene);
        session.Net.StartSingleplayer("Player");
        await session.PhysicsFrame();

        int before = Log.Tail().Count;
        session.Net.HostZombies("/nonexistent-map");

        for (int i = 0; i < 20; i++)
            await session.PhysicsFrame();

        // Never armed: these endpoints appear nowhere. HostZombies returns before the PATH_PROBE block
        // when the level ships no zombie data, and a diagnostic that crashed a session it could not
        // measure would be worse than no diagnostic — but so would one that quietly probed anyway, which
        // is what asserting IsActive could not tell apart.
        Assert.DoesNotContain(Log.Tail(), line => line.Contains("(13, 0, 13)", StringComparison.Ordinal));
        Assert.True(session.Net.IsActive);
    }

    // An environment flag set for one test and put back afterwards: these are read once, where the
    // session is brought up, and one left set would silently change every test after it in this process.
    private sealed class EnvFlagScope : System.IDisposable
    {
        private readonly string _name;
        private readonly string _previous;

        public EnvFlagScope(string name, string value)
        {
            _name = name;
            _previous = OS.GetEnvironment(name);
            OS.SetEnvironment(name, value);
        }

        public void Dispose() => OS.SetEnvironment(_name, _previous);
    }


    private static bool RealMap(out string levelDir)
    {
        levelDir = "";
        string? install = Assets.UnturnedInstall.Find();
        string? maps = install == null ? null : System.IO.Path.Combine(install, "Maps", "PEI");
        if (maps == null || !System.IO.Directory.Exists(maps))
        {
            if (System.Environment.GetEnvironmentVariable("UG_REQUIRE_REAL_DATA") == "1")
            {
                throw new System.IO.IOException(
                    "UG_REQUIRE_REAL_DATA=1 but the PEI map is not present; this run exists to prove these "
                    + "tests execute");
            }

            Log.Print("[runtime-tests] skipping: no PEI map "
                + "(set UNTURNED_PATH or run ./scripts/fetch-game-data.sh)");
            return false;
        }

        levelDir = maps;
        return true;
    }

    // --- helpers -------------------------------------------------------------------------------------

    // A manager in the tree, configured over flat ground, freed with the test. Freeing is what closes
    // its transports: a session whose sockets outlived it would hold ports for the rest of the run.
    private sealed class Session : System.IDisposable
    {
        private readonly Node _testScene;

        public NetworkManager Net { get; }

        public Session(Node testScene)
        {
            _testScene = testScene;
            Net = new NetworkManager { LevelName = "PEI" };
            testScene.AddChild(Net);
            Net.Configure(new HeightmapSampler(System.Array.Empty<HeightmapTile>()), Vector3.Zero);
        }

        public SignalAwaiter PhysicsFrame() =>
            _testScene.ToSignal(_testScene.GetTree(), SceneTree.SignalName.PhysicsFrame);

        public void Dispose()
        {
            _testScene.RemoveChild(Net);
            Net.Free(); // _ExitTree closes both transports and frees the navigation
        }
    }

    private SignalAwaiter NextFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
}
