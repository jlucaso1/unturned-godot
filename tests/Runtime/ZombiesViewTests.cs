using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Net;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The replicated zombie population.
//
// What is covered here is the LIFETIME problem, which is the one the remote players do not have: the
// character templates.
//
// Anything about where a zombie is DRAWN needs the injectable session clock (see the RemotePlayersView
// tests, which is where that lands) plus a replicated population, and waits on both.
//
// Two looks — normal and mega — are imported once and cloned per zombie, and they are deliberately NOT
// children of the view. Unparented Nodes are not reclaimed when the tree is torn down, so the view has to
// free them itself; a view that forgot would leak two full character rigs on every session, and a view
// that parented them would draw two zombies standing at the origin of every map.
public class ZombiesViewTests : TestClass
{
    public ZombiesViewTests(Node testScene) : base(testScene) { }

    // A view with no zombies replicated draws nothing. The client joins before any ZombieList arrives,
    // so this is every session's first frames.
    [Test]
    public async Task AViewWithNothingReplicatedDrawsNothing()
    {
        using var harness = new Harness(TestScene);
        await harness.Draw();

        Assert.Equal(0, harness.View.GetChildCount());
    }

    // Warming the templates is safe to call when the game is not installed: it is done behind the loading
    // screen precisely so it never happens on the frame a city's zombies arrive, and a missing install
    // must leave the view drawing placeholders rather than failing the load.
    [Test]
    public async Task WarmingTemplatesWithoutTheGameIsSurvivable()
    {
        using var harness = new Harness(TestScene, unturnedPath: "/nonexistent-unturned");

        harness.View.WarmupTemplates();
        await harness.Draw();

        Assert.Equal(0, harness.View.GetChildCount());
    }

    // The templates are freed with the view. They are not children — an unparented Node is not reclaimed
    // by tree teardown — so a view that forgot would leak two full character rigs per session.
    [Test]
    public async Task LeavingTheTreeReleasesTheTemplates()
    {
        var harness = new Harness(TestScene, unturnedPath: "/nonexistent-unturned");
        harness.View.WarmupTemplates();
        await harness.Draw();

        // Nothing observable to read back — what this covers is that teardown runs to completion over a
        // view whose templates may or may not have imported.
        harness.Dispose();
        await harness.Draw();
    }

    // --- a population that actually replicates -------------------------------------------------------

    // Zombies the server spawned turn into avatars on the client. Nothing about a zombie is sent as an
    // object: the server streams positions and states for a region, and the view builds and destroys the
    // things that draw them.
    [Test]
    public async Task ReplicatedZombiesBecomeAvatars()
    {
        using var harness = new Harness(TestScene, withZombies: true);
        await harness.Draw(ticks: 20);

        Assert.True(harness.View.GetChildCount() > 0,
            "the server spawned zombies and the client drew none of them");
    }

    // Leaving the region drops its avatars, without the server saying anything. ZombieManager does this
    // on the LOCAL player's bound transition precisely so a client walking away stops paying for a city
    // it can no longer see — the server independently notices the same transition and resends on return.
    [Test]
    public async Task WalkingOutOfTheRegionDropsItsAvatars()
    {
        using var harness = new Harness(TestScene, withZombies: true);
        await harness.Draw(ticks: 20);
        Assert.True(harness.View.GetChildCount() > 0, "nothing was drawn to begin with");

        harness.LocalPosition = new Vector3(5000f, 0f, 5000f); // far outside every bound
        await harness.Draw(ticks: 4);

        Assert.Equal(0, harness.View.GetChildCount());
    }

    // A session reset forgets everything. Whatever answers the next Hello may be a restarted host, which
    // numbers its zombies from zero again — so every id this view holds stops meaning anything, and
    // keeping them would refuse the new session's zombies one by one.
    [Test]
    public async Task ASessionResetForgetsEveryZombie()
    {
        using var harness = new Harness(TestScene, withZombies: true);
        await harness.Draw(ticks: 20);
        Assert.True(harness.View.GetChildCount() > 0, "nothing was drawn to begin with");

        harness.BreakTheSession();
        await harness.Draw(ticks: 60);

        // The zombies COME BACK. A child count is never negative, so the old assertion held whether the
        // view forgot the session or refused every resent zombie one by one — which is the regression it
        // was written to catch.
        Assert.True(harness.View.GetChildCount() > 0,
            "the view rejoined and drew nothing, so it is still refusing the old session's ids");
    }

    // --- helpers -------------------------------------------------------------------------------------

    private sealed class Harness : IDisposable
    {
        private readonly Node _testScene;
        private readonly LoopbackServerTransport _transport = new();
        private readonly NetServer _server;
        private double _now = 5000.0;
        private bool _disposed;

        public NetClient Client { get; }
        public ZombiesView View { get; }

        public Vector3 LocalPosition { get; set; }

        public Harness(Node testScene, string unturnedPath = "", bool withZombies = false)
        {
            _testScene = testScene;
            _server = new NetServer(_transport,
                new ServerSimulation(new HeightfieldMoveSolver(FlatGround)), new Vector3(0f, 10f, 0f), "PEI");
            Client = new NetClient(_transport.CreateClient(), "Local", "PEI");

            IReadOnlyList<NavBound> bounds = withZombies
                ? new[] { new NavBound { Center = Vector3.Zero, Size = new Vector3(256f, 64f, 256f) } }
                : Array.Empty<NavBound>();

            View = ZombiesView.Create(Client, unturnedPath, null, bounds, () => LocalPosition);
            testScene.AddChild(View);

            if (withZombies)
                SpawnPopulation(bounds);
        }

        // A small population inside one bound, hosted the way every session hosts one: the ZombieHost
        // hooks the server's tick seams, so solo, LAN and dedicated all replicate through this path.
        private void SpawnPopulation(IReadOnlyList<NavBound> bounds)
        {
            var tables = new List<ZombieTable> { new() { Name = "Normal", Health = 100, Damage = 10 } };
            var zombies = new ZombieSystem(tables, bounds, FlatGround);

            var spawns = new List<ZombieSpawnpointData>();
            for (int i = 0; i < 8; i++)
                spawns.Add(new ZombieSpawnpointData(0, new Vector3(i * 2f, 0f, i * 2f)));
            zombies.Spawn(spawns, new Random(1));

            Host = new ZombieHost(zombies, _server);
        }

        // Held only so it outlives the constructor: it drives itself off the server's tick seams, and a
        // host nothing referenced would be collected out from under the session it is replicating.
        public ZombieHost? Host { get; private set; }

        // What a lost connection looks like from here: the client gives up and rejoins from scratch.
        public void BreakTheSession()
        {
            _now += NetClient.StateTimeout + 1.0;
            Client.Update(_now);
        }

        public SignalAwaiter Draw(int ticks = 2)
        {
            for (int i = 0; i < ticks; i++)
            {
                _now += ServerSimulation.TickRate;
                _server.Update(_now);
                Client.Update(_now);
            }
            return _testScene.ToSignal(_testScene.GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        private static bool FlatGround(float x, float z, out float y)
        {
            y = 10f;
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _testScene.RemoveChild(View);
            View.Free();
        }
    }
}
