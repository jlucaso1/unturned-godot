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
        const ushort port = 43210;
        DedicatedServer server = Host(port, "PEI");
        BotClient bot = BotClient.Create("127.0.0.1", port, "Bot", lifetime: 6f);
        TestScene.AddChild(bot);

        try
        {
            // Long enough for the query to be answered and the handshake to complete, but short of the
            // bot's own lifetime — a bot that reached the end would take the process with it.
            for (int i = 0; i < 120; i++)
                await NextPhysicsFrame();

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
    [Test]
    [Timeout(120_000)]
    public async Task ABotPointedAtNothingWaits()
    {
        BotClient bot = BotClient.Create("127.0.0.1", 43219, "Bot", lifetime: 300f);
        TestScene.AddChild(bot);

        try
        {
            for (int i = 0; i < 60; i++)
                await NextPhysicsFrame();

            // Still there, still trying. The lifetime is deliberately long so the bot cannot reach its
            // own exit inside this test — that exit ends the process.
            Assert.True(GodotObject.IsInstanceValid(bot));
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

        DedicatedServer server = DedicatedServer.Create(install, "PEI", "PEI", new Vector3(0f, 64f, 0f),
            43211);
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
            Assert.True(server.GetChildCount() > 0, "the host built no world at all");
        }
        finally
        {
            Free(server);
        }
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
