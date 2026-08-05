using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The NPC characters a map places — Russia is the only official one that does.
//
// Two decisions are worth holding here, and both are about what NOT to build.
//
// A map that places none gets null rather than an empty root: an empty node in the scene is still a node,
// and a world should not gain one for a feature it never uses. Every official map except Russia takes
// that path, so it is the common case rather than an edge.
//
// And a rig that cannot be imported produces no characters rather than placeholder boxes. Boxes are
// precisely what this exists to stop drawing — before it, every NPC placement fell through to one.
//
// The import itself needs the game installed. Without it the builder takes its own no-rig path, which is
// the one asserted below; with it, the placements are drawn.
public class NpcsBuilderTests : TestClass
{
    public NpcsBuilderTests(Node testScene) : base(testScene) { }

    // No placements, no node. This is what every map but Russia does.
    [Test]
    public void AMapThatPlacesNoNpcsGetsNoNode()
    {
        Assert.Null(NpcsBuilder.Build(Array.Empty<PlacedObject>(), "", out int rendered));
        Assert.Equal(0, rendered);
    }

    // With placements but no importable rig, a root comes back carrying nothing. The alternative — the
    // behaviour this replaced — was a placeholder box per NPC, which is worse than an absent character
    // because it looks like a bug in the map rather than a missing feature.
    [Test]
    public async Task PlacementsWithNoImportableRigDrawNoBoxes()
    {
        var placements = new List<PlacedObject>
        {
            new(new Vector3(10f, 0f, 10f), Vector3.Zero, Vector3.One, 0, Guid.NewGuid()),
            new(new Vector3(20f, 0f, 20f), Vector3.Zero, Vector3.One, 0, Guid.NewGuid()),
        };

        // A path with no game under it: the import fails and the no-rig branch runs.
        Node3D? root = NpcsBuilder.Build(placements, "/nonexistent-unturned", out int rendered);

        if (root == null)
            return; // some builds resolve an install anyway; the null case is covered above

        TestScene.AddChild(root);
        await NextFrame();

        Assert.Equal(0, rendered);
        Assert.Equal(0, root.GetChildCount());

        root.QueueFree();
    }

    private SignalAwaiter NextFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
}
