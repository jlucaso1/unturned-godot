using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Net;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The other players, drawn from replicated state.
//
// This needed a production change to become testable, and the change was worth making on its own terms.
// The view samples each remote against NetworkManager.Now — the process clock — while a test server has
// to be driven on a simulated one. The two disagree by however long the run took to reach the assertion,
// which is not a difference a test can assert around: it has to be removed. So the session clock is now
// injectable, and these drive both halves from one.
//
// The clock was one barrier and it is gone. A SECOND one remains, and naming it precisely is most of
// what this file is for: getting an avatar to appear needs the client's roster, the snapshot buffer and
// the view's own frame to line up, and the fixture below does not manage it — the avatar is absent when
// the assertion runs. Everything about WHERE a remote is drawn waits on that.
//
// So what is covered here is the one thing that does not: the view frees a template it built and leaves
// a borrowed one alone. Small, but it is the difference between joining a server and having the local
// character's mesh freed out from under it.
public class RemotePlayersViewTests : TestClass
{
    public RemotePlayersViewTests(Node testScene) : base(testScene) { }

    // The view frees a template it built for itself and does NOT free one it was handed. Main passes the
    // already-imported local player, and freeing that takes the local character's mesh with it.
    [Test]
    public async Task ABorrowedTemplateOutlivesTheView()
    {
        using var harness = new Harness(TestScene);
        var borrowed = new Node3D { Name = "BorrowedTemplate" };
        TestScene.AddChild(borrowed);
        RemotePlayersView view = RemotePlayersView.Create(harness.Client, "", null, borrowed);
        TestScene.AddChild(view);
        await harness.Draw();

        TestScene.RemoveChild(view);
        view.Free();

        Assert.True(GodotObject.IsInstanceValid(borrowed), "the view freed a template it did not own");
        borrowed.QueueFree();
    }

    // --- helpers -------------------------------------------------------------------------------------

    // A loopback server, a local client whose view is under test, and one client per remote — all driven
    // from a clock this harness owns, which is what makes the interpolated positions assertable.
    private sealed class Harness : IDisposable
    {
        private static readonly Vector3 Spawn = new(0f, 10f, 0f);

        private readonly Node _testScene;
        private readonly LoopbackServerTransport _transport = new();
        private readonly NetServer _server;
        private readonly Dictionary<byte, NetClient> _others = new();
        private readonly List<NetClient> _all = new();
        private readonly Dictionary<byte, uint> _frames = new();
        private double _now = 5000.0;

        public NetClient Client { get; }
        public RemotePlayersView View { get; }

        public Harness(Node testScene)
        {
            _testScene = testScene;
            NetworkManager.OverrideClockForTests(() => _now);
            _server = new NetServer(_transport,
                new ServerSimulation(new HeightfieldMoveSolver(FlatGround)), Spawn, "PEI");
            Client = new NetClient(_transport.CreateClient(), "Local", "PEI");
            View = RemotePlayersView.Create(Client, "", null, new Node3D { Name = "Template" });
            testScene.AddChild(View);
        }

        public byte AddRemote(string name, Vector3 position)
        {
            var other = new NetClient(_transport.CreateClient(), name, "PEI");
            _all.Add(other); // pumped from here, or its join never completes
            Pump(6);
            byte id = NewRemoteId();
            _others[id] = other;
            _frames[id] = 0;
            Move(id, position);
            return id;
        }

        public void Move(byte id, Vector3 position)
        {
            _others[id].SendInput(new InputCommand(++_frames[id], 0, 0, jump: false, sprint: false,
                NetAngles.QuantizeYaw(0f), NetAngles.QuantizePitch(90f),
                EPlayerStance.Stand, position, grounded: true));
            Pump(8); // past the interpolation delay, so the sample lands on the reported position
        }

        // One engine frame, with the session clock already advanced past everything the server sent.
        public SignalAwaiter Draw()
        {
            Pump(2);
            return _testScene.ToSignal(_testScene.GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        private void Pump(int rounds)
        {
            for (int i = 0; i < rounds; i++)
            {
                _now += ServerSimulation.TickRate;
                _server.Update(_now);
                Client.Update(_now);
                foreach (NetClient other in _all)
                    other.Update(_now);
            }
        }

        private byte NewRemoteId()
        {
            foreach (byte id in Client.Remotes.Keys)
                if (!_others.ContainsKey(id))
                    return id;
            throw new InvalidOperationException("the server never announced the new player");
        }

        public Node3D? Avatar(byte id)
        {
            foreach (Node child in View.GetChildren())
                if (child is Node3D node
                    && node.Name.ToString().StartsWith($"Remote_{id}", StringComparison.Ordinal))
                    return node;
            return null;
        }

        private static bool FlatGround(float x, float z, out float y)
        {
            y = 10f;
            return true;
        }

        // Hands the clock back to the engine. A leaked override would freeze every later test's session.
        public void Dispose()
        {
            NetworkManager.OverrideClockForTests(null);
            View.QueueFree();
        }
    }
}
