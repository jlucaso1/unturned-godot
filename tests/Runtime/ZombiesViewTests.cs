using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Net;
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

        public Harness(Node testScene, string unturnedPath = "")
        {
            _testScene = testScene;
            _server = new NetServer(_transport,
                new ServerSimulation(new HeightfieldMoveSolver(FlatGround)), new Vector3(0f, 10f, 0f), "PEI");
            Client = new NetClient(_transport.CreateClient(), "Local", "PEI");
            View = ZombiesView.Create(Client, unturnedPath, null, Array.Empty<NavBound>(),
                () => Vector3.Zero);
            testScene.AddChild(View);
        }

        public SignalAwaiter Draw()
        {
            for (int i = 0; i < 2; i++)
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
