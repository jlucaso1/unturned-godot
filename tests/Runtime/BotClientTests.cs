using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The headless bot: a client that joins a server, walks around and reports what it saw.
//
// It exists to put load on a real server without a person driving it, which makes its own robustness the
// thing worth testing.
//
// What is NOT tested here is the end of its lifetime, and the reason is worth recording: when a bot is
// done it quits the PROCESS. That is right for its actual use — it is the whole program in a soak run —
// and it means a test that let one run to completion would take the suite down with it. Asserting that
// behaviour needs the bot launched as its own process, which is a harness rather than a test.
//
// The wire-reading half carries a guard worth naming: a subscriber that reads packets owns its own decode
// guard, because NetClient's is scoped to the messages IT handles. A truncated ZombieList arriving here
// unguarded would end the process — which is exactly the failure a bot exists to survive.
public class BotClientTests : TestClass
{
    public BotClientTests(Node testScene) : base(testScene) { }

    // Creating one does not block on the server answering. It asks which level the server runs and waits
    // for a reply, so a wrong address costs the caller nothing up front.
    [Test]
    public async Task CreatingABotDoesNotBlockOnTheServer()
    {
        // A port nothing is listening on, which is the case this has to survive.
        BotClient bot = BotClient.Create("127.0.0.1", 42999, "SoakBot", lifetime: 0.5f);
        TestScene.AddChild(bot);

        await NextPhysicsFrame();

        Assert.True(GodotObject.IsInstanceValid(bot), "the bot died on construction");
        bot.QueueFree();
    }

    // Leaving the tree closes the socket. A bot freed without that leaks a UDP port for the process, and
    // a soak run starts hundreds of them.
    [Test]
    public async Task LeavingTheTreeClosesTheSocket()
    {
        BotClient bot = BotClient.Create("127.0.0.1", 42998, "SoakBot", lifetime: 60f);
        TestScene.AddChild(bot);
        await NextPhysicsFrame();

        TestScene.RemoveChild(bot);
        bot.Free();

        // Nothing to read back through the API; what this covers is that teardown runs without throwing
        // on a transport that never connected to anything.
        await NextPhysicsFrame();
    }

    private SignalAwaiter NextPhysicsFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.PhysicsFrame);
}
