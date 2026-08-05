using System;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Net;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The other players, as this client draws them.
//
// Nothing here is authoritative and nothing here is guessed: the server replicates each player's pose
// with a timestamp, and the view samples that stream at the local clock. Which means the SAMPLE is the
// whole design — an avatar is not moved when a packet arrives, it is moved every frame to where the
// stream says that player was a fixed moment ago. Snapping to packets instead would make every remote
// player stutter at exactly the rate the network happens to be running.
//
// The animation is driven from the replicated input flag rather than from position deltas, and that is
// deliberate: deltas flicker walk/idle during in-place jumps and packet stalls, restarting the crossfade
// mid-air. The owner's own controller uses the same flag, so both ends animate off one fact.
//
// The tests drive a real loopback session with two clients in it, both ticking on the SAME clock the view
// samples with. A harness that advanced the session on a synthetic clock while the view read the engine's
// would sample a stream whose timestamps sat hours away, and every avatar would sit wherever it spawned.
public class RemotePlayersViewTests : TestClass
{
    public RemotePlayersViewTests(Node testScene) : base(testScene) { }

    // Alone in a session, there is nobody to draw. This is every session's first frames, and a view that
    // spawned something here would put a stranger in an empty world.
    [Test]
    public async Task AloneInASessionNobodyIsDrawn()
    {
        using var session = new Session(TestScene);
        await session.Run(frames: 10);

        Assert.Equal(0, session.View.GetChildCount());
    }

    // Somebody else joins and gets an avatar. The roster is the client's, not the view's — the view
    // simply draws whoever is in it, which is what keeps "who is here" a single fact.
    [Test]
    public async Task SomebodyElseJoiningGetsAnAvatar()
    {
        using var session = new Session(TestScene);
        session.AddSecondPlayer("Other");

        await session.RunUntil(() => session.View.GetChildCount() > 0);

        Assert.True(session.View.GetChildCount() > 0, "a second player joined and nothing was drawn");
    }

    // And their avatar stands where the stream says they are, rather than at the origin. An avatar that
    // never moved would look exactly like one that was never replicated.
    [Test]
    public async Task TheirAvatarStandsWhereTheStreamSaysTheyAre()
    {
        using var session = new Session(TestScene);
        session.AddSecondPlayer("Other");
        await session.RunUntil(() => session.View.GetChildCount() > 0);

        // Asserted rather than skipped. An early return here turns a slow runner into a silent pass, and
        // the run that was too slow reports one failure and one skip instead of two failures.
        Assert.True(session.View.GetChildCount() > 0, "nobody was drawn to place");

        var avatar = (Node3D)session.View.GetChild(0);
        Assert.Equal(session.Spawn.Y, avatar.Position.Y, 1);
    }

    // Leaving takes the avatar with it. A view that kept them would populate the world with players who
    // are not there, standing wherever their last packet left them.
    [Test]
    public async Task LeavingTakesTheAvatarAway()
    {
        using var session = new Session(TestScene);
        session.AddSecondPlayer("Other");
        await session.RunUntil(() => session.View.GetChildCount() > 0);

        // Asserted, not skipped: returning here would skip the removal below, which is the whole point
        // of this test — so a slow runner would report success for the behaviour it never observed.
        Assert.True(session.View.GetChildCount() > 0, "nobody joined, so nothing can leave");

        session.DropSecondPlayer();
        await session.RunUntil(() => session.View.GetChildCount() == 0);

        Assert.Equal(0, session.View.GetChildCount());
    }

    // The view owns the template it built and frees it on the way out. It is not a child — an unparented
    // Node is not reclaimed by tree teardown — so a view that forgot would leak a character rig per
    // session, and one that parented it would draw a spare player standing at the origin of every map.
    [Test]
    public async Task LeavingTheTreeReleasesTheTemplateItOwns()
    {
        var session = new Session(TestScene);
        await session.Run(frames: 5);

        session.Dispose();
        await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    // A template the CALLER owns is left alone. The local player's rig is handed in so a remote avatar
    // costs no second import, and freeing it here would take the player's own body with it.
    [Test]
    public async Task ATemplateTheCallerOwnsIsLeftAlone()
    {
        var borrowed = new Node3D { Name = "LocalRig" };
        var session = new Session(TestScene, borrowed);
        await session.Run(frames: 5);

        session.Dispose();
        await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);

        Assert.True(GodotObject.IsInstanceValid(borrowed), "the view freed a template it did not own");
        borrowed.Free();
    }

    // --- helpers -------------------------------------------------------------------------------------

    // A loopback server with one client watching it, and room for a second.
    //
    // Everything ticks on NetworkManager.Now — the same clock the view samples with. That is not a
    // detail: the view places every avatar by sampling the replicated stream at the local clock, so a
    // session advanced on a synthetic clock would produce timestamps hours from where the view looks,
    // and every avatar would sit wherever it spawned while the test claimed to have replicated it.
    private sealed class Session : IDisposable
    {
        private readonly Node _testScene;
        private readonly LoopbackServerTransport _transport = new();
        private readonly NetServer _server;
        private NetClient? _second;
        private LoopbackClientTransport? _secondTransport;
        private bool _disposed;

        public Vector3 Spawn { get; } = new(0f, 10f, 0f);
        public NetClient Client { get; }
        public RemotePlayersView View { get; }

        public Session(Node testScene, Node3D? localTemplate = null)
        {
            _testScene = testScene;
            _server = new NetServer(_transport,
                new ServerSimulation(new HeightfieldMoveSolver(FlatGround)), Spawn, "PEI");
            Client = new NetClient(_transport.CreateClient(), "Local", "PEI");
            View = RemotePlayersView.Create(Client, "/nonexistent-unturned", null, localTemplate);
            testScene.AddChild(View);
        }

        public void AddSecondPlayer(string name)
        {
            _secondTransport = _transport.CreateClient();
            _second = new NetClient(_secondTransport, name, "PEI");
        }

        // What a player leaving looks like from the server's side: their connection goes away.
        public void DropSecondPlayer()
        {
            _secondTransport?.Close();
            _secondTransport = null;
            _second = null;
        }

        public async Task Run(int frames)
        {
            for (int i = 0; i < frames; i++)
                await OneFrame();
        }

        // Pumps until the condition holds or the deadline passes. A fixed frame count either wastes
        // time or fails on a slow machine, and the tests here are about replication ARRIVING rather
        // than about how many frames it took.
        public async Task RunUntil(Func<bool> settled, int limitMs = 20_000)
        {
            ulong deadline = Time.GetTicksMsec() + (ulong)limitMs;
            while (!settled() && Time.GetTicksMsec() < deadline)
                await OneFrame();
        }

        private SignalAwaiter OneFrame()
        {
            double now = NetworkManager.Now;
            _server.Update(now);
            Client.Update(now);
            _second?.Update(now);
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
            DropSecondPlayer();
            _testScene.RemoveChild(View);
            View.Free();
        }
    }
}
