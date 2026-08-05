using System;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Net;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// A real host, and a real client joining it over a real socket.
//
// Every other network test in this suite runs over loopback transports — two objects handing each other
// byte arrays in one process. That is the right shape for testing the PROTOCOL, and it is the wrong
// shape for testing whether the game can be joined: it never binds a port, never fragments a datagram,
// never asks the server which level it runs before committing to a load, and never exercises the query
// that a server browser would.
//
// So these bring up the headless authority on a UDP port and drive the scripted client at it. The client
// is the same one a multiplayer verification run uses — it exists precisely because a human cannot be
// half of an automated check — and what it does first is the thing worth holding: it ASKS which level
// the server runs, and builds that one, rather than assuming and being refused.
public class DedicatedSessionTests : TestClass
{
    public DedicatedSessionTests(Node testScene) : base(testScene) { }

    // A bot joins a real server over a real socket and is admitted. This is the whole multiplayer path
    // end to end — bind, query, handshake, admit — and none of it runs over a loopback transport.
    [Test]
    [Timeout(300_000)]
    public async Task ABotJoinsARealServerOverARealSocket()
    {
        ushort port = FreePort();
        DedicatedServer server = Host(port, "PEI");
        // Comfortably beyond this test body. The bot ENDS THE PROCESS when its lifetime expires — that
        // is how an automated multiplayer check reports itself finished — so a lifetime tuned to the
        // expected duration turns a slow CI runner into a suite that stops mid-run with no failure. The
        // [Timeout] above is what bounds a hang; this only has to be longer than that cannot be reached.
        BotClient bot = BotClient.Create("127.0.0.1", port, "Bot", lifetime: 3600f);
        TestScene.AddChild(bot);

        try
        {
            // Wait for the join itself rather than for a frame count: what is being proven is that the
            // handshake completed, and a fixed count either wastes time or fails on a slow runner.
            ulong deadline = Time.GetTicksMsec() + 20_000;
            while (!bot.Joined && Time.GetTicksMsec() < deadline)
                await NextPhysicsFrame();

            Assert.True(bot.Joined,
                "the bot never joined, so the query, the handshake or the admission is broken");
            Assert.NotNull(server.PunchDamage);
        }
        finally
        {
            TestScene.RemoveChild(bot);
            bot.Free();
            Free(server);
        }
    }

    // A bot pointed at nothing waits rather than failing outright, for the same reason a player's join
    // does: a host that is not there is indistinguishable from one that is slow to answer.
    //
    // The wait is bounded by the QUERY timeout rather than by a frame count, and that is a hard
    // constraint rather than tidiness. A bot whose query times out reports the failure the only way an
    // unattended check can — by ending the process — so a frame loop that outran ServerQuery.Timeout on
    // a slow runner would take the whole suite with it and report nothing. This samples the waiting state
    // well inside that window and measures it in SECONDS, not frames.
    [Test]
    [Timeout(120_000)]
    public async Task ABotPointedAtNothingWaits()
    {
        BotClient bot = BotClient.Create("127.0.0.1", FreePort(), "Bot", lifetime: 3600f);
        TestScene.AddChild(bot);

        try
        {
            ulong deadline = Time.GetTicksMsec() + (ulong)(ServerQuery.Timeout * 1000.0 / 4.0);
            while (Time.GetTicksMsec() < deadline)
                await NextPhysicsFrame();

            // Still there, still trying, and NOT joined — which is the state being sampled. Without the
            // second half this would pass just as well against a server that answered.
            Assert.True(GodotObject.IsInstanceValid(bot));
            Assert.False(bot.Joined, "something answered on the port this test needs to be empty");
        }
        finally
        {
            TestScene.RemoveChild(bot);
            bot.Free();
        }
    }

    // The real map, hosted. A dedicated server on a level that has content loads its terrain collision,
    // its object collision and its zombie population — and none of that is reachable from the empty-map
    // path, which is what every other test of this host takes.
    [Test]
    [Timeout(300_000)]
    public async Task TheRealMapIsHostedWithItsCollisionAndItsZombies()
    {
        if (!RealInstall(out string install))
            return;

        DedicatedServer server = DedicatedServer.Create(install, "PEI", "PEI", new Vector3(0f, 64f, 0f), FreePort());
        TestScene.AddChild(server);

        try
        {
            // Ticking runs the navigation reconciliation the host starts on its first physics frame.
            for (int i = 0; i < 60; i++)
                await NextPhysicsFrame();

            Assert.NotNull(server.PunchDamage);

            // The world it built is under the node itself: terrain collision and object collision, both
            // in the same World3D the authoritative zombie queries use. A host that built them elsewhere
            // would answer every query against an empty world while looking fine.
            //
            // Counted as BODIES rather than as children, because the roots are added unconditionally —
            // a host whose terrain failed to read still has both containers, and a child count would
            // report that as a world.
            Assert.True(Bodies(server) > 0, "the host built collision roots with nothing in them");
        }
        finally
        {
            Free(server);
        }
    }

    // A port the OS says is free, rather than one this file hopes is. DedicatedServer.Create binds
    // immediately, so an occupied port fails the test at construction — and two runtime-test processes on
    // one machine would collide on any fixed number.
    private static ushort FreePort()
    {
        using var probe = new System.Net.Sockets.UdpClient(0, System.Net.Sockets.AddressFamily.InterNetwork);
        return (ushort)((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    // Every static body under a node, however deep. The roots nest by region and by placement, so a
    // shallow count would miss all of it.
    private static int Bodies(Node node)
    {
        int found = node is StaticBody3D ? 1 : 0;
        foreach (Node child in node.GetChildren())
            found += Bodies(child);
        return found;
    }

    // --- helpers -------------------------------------------------------------------------------------

    private DedicatedServer Host(ushort port, string level)
    {
        DedicatedServer server = DedicatedServer.Create("/nonexistent-unturned", level, level,
            new Vector3(0f, 10f, 0f), port);
        TestScene.AddChild(server);
        return server;
    }

    private void Free(DedicatedServer server)
    {
        TestScene.RemoveChild(server);
        server.Free(); // _ExitTree closes the transport and releases the navigation state
    }

    private static bool RealInstall(out string install)
    {
        install = "";
        string? found = Assets.UnturnedInstall.Find();
        if (found == null)
        {
            if (System.Environment.GetEnvironmentVariable("UG_REQUIRE_REAL_DATA") == "1")
            {
                throw new System.IO.IOException(
                    "UG_REQUIRE_REAL_DATA=1 but no Unturned install is present; this run exists to prove "
                    + "these tests execute");
            }

            Log.Print("[runtime-tests] skipping: no Unturned install "
                + "(set UNTURNED_PATH or run ./scripts/fetch-game-data.sh)");
            return false;
        }

        install = found;
        return true;
    }

    private SignalAwaiter NextPhysicsFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.PhysicsFrame);
}
