using System;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The cold load's conductor: it decodes the bundle on a worker, builds the scene when the meshes land,
// and paces the texture uploads across frames afterwards.
//
// The parts worth pinning without a whole world behind it are the ones about SEQUENCE and CANCELLATION,
// because those are where a load goes wrong in ways that look like a hang rather than a crash:
//
//   - Nothing may apply textures before the scene exists, which is why _Process is gated on it. An
//     ungated frame walks a registry pointed at meshes nobody has built.
//   - A load that is abandoned — the player backs out to the menu mid-load — has to stop its worker and
//     settle, rather than finishing into a scene tree that has been torn down.
//   - Cancelling before anything started, and cancelling twice, both have to be quiet. The menu can send
//     either.
public class ObjectStreamerTests : TestClass
{
    public ObjectStreamerTests(Node testScene) : base(testScene) { }

    // A fresh streamer has nothing to say: no needed GUIDs, no damageable world, and its completion is
    // still outstanding. The caller checks these before deciding whether to show a loading screen.
    [Test]
    public void AFreshStreamerHasNothingToReport()
    {
        var streamer = new ObjectStreamer { Name = "ObjectStreamer" };
        TestScene.AddChild(streamer);

        Assert.Empty(streamer.NeededGuids);
        Assert.Null(streamer.Damageable);
        Assert.False(streamer.Completion.IsCompleted);

        streamer.QueueFree();
    }

    // Textures are not applied until the scene exists. The frame loop is gated on that, and an ungated
    // one would walk a registry pointing at meshes nobody has built yet.
    [Test]
    public async Task NoTexturesAreAppliedBeforeTheSceneExists()
    {
        var streamer = new ObjectStreamer { Name = "ObjectStreamer" };
        int progress = 0;
        streamer.Progress += (_, _) => progress++;
        TestScene.AddChild(streamer);

        for (int i = 0; i < 5; i++)
            await NextFrame();

        Assert.Equal(0, progress);
        Assert.False(streamer.Completion.IsCompleted);

        streamer.QueueFree();
    }

    // Cancelling a streamer that never started is quiet. The menu sends this whenever a player backs out
    // of a map, including before anything was prepared.
    [Test]
    public async Task CancellingBeforeAnythingStartedIsQuiet()
    {
        var streamer = new ObjectStreamer { Name = "ObjectStreamer" };
        TestScene.AddChild(streamer);

        await streamer.CancelAsync();

        streamer.QueueFree();
    }

    // And cancelling twice is quiet too — the menu's teardown and the node's own exit can both arrive.
    [Test]
    public async Task CancellingTwiceIsQuiet()
    {
        var streamer = new ObjectStreamer { Name = "ObjectStreamer" };
        TestScene.AddChild(streamer);

        await streamer.CancelAsync();
        await streamer.CancelAsync();

        streamer.QueueFree();
    }

    // Beginning without a prepare is not a crash. It is what a caller that failed to read the level does,
    // and the load has to end rather than wait forever on a decode nobody started.
    [Test]
    public async Task BeginningWithoutAPrepareSettlesRatherThanHanging()
    {
        var streamer = new ObjectStreamer { Name = "ObjectStreamer" };
        TestScene.AddChild(streamer);

        Task begin = streamer.BeginAsync();
        ulong deadline = Time.GetTicksMsec() + 5000;
        while (!begin.IsCompleted && Time.GetTicksMsec() < deadline)
            await NextFrame();

        Assert.True(begin.IsCompleted, "BeginAsync never settled without a prepare");
        streamer.QueueFree();
    }

    // The layer-texture handoff is a task the terrain build awaits. It must exist from the start, or the
    // terrain would have nothing to await and would build untextured before the streamer ever ran.
    [Test]
    public void TheLayerTextureHandoffExistsFromTheStart()
    {
        var streamer = new ObjectStreamer { Name = "ObjectStreamer" };
        TestScene.AddChild(streamer);

        Assert.NotNull(streamer.LayerTextures);
        Assert.False(streamer.LayerTextures.IsCompleted);

        streamer.QueueFree();
    }

    // The navigation field is optional and settable: navmesh reconciliation hands one in so it receives
    // the same heightfield the physics server does, without needing a physics tick to read the ground.
    [Test]
    public void TheNavigationFieldIsOptional()
    {
        var streamer = new ObjectStreamer { Name = "ObjectStreamer" };
        TestScene.AddChild(streamer);

        Assert.Null(streamer.NavigationField);
        streamer.NavigationField = new Data.CollisionFieldBuilder();
        Assert.NotNull(streamer.NavigationField);

        streamer.QueueFree();
    }

    private SignalAwaiter NextFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
}
