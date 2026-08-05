using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Chickensoft.GoDotTest;
using Godot;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The floating place names — a town's label readable from across the island, like Unturned's own.
//
// Reading Nodes.dat is pure and unit tested in core/. What is only testable here is what the labels
// actually ARE, and every one of those settings is what makes a name legible rather than decorative:
// billboarded so it faces the camera from any angle, fixed-size so it does not shrink to nothing at
// distance, and depth-tested off so a hill does not swallow the name of the town behind it.
//
// A label that reverted any of those would still be in the scene and still say the right word, which is
// why they are asserted rather than left to be noticed.
//
// They are also BUILT HIDDEN. The game names a place when you arrive somewhere rather than hanging a
// permanent set of billboards over the island, so this is a view of the level's data; `locations.enabled 1`
// in the console brings it back.
public class NodesBuilderTests : TestClass
{
    public NodesBuilderTests(Node testScene) : base(testScene) { }

    [Test]
    public void ThePlaceNamesAreBuiltHiddenAndReachableByName()
    {
        using var dir = new TempDir();
        dir.WriteNodes(("Belfast", new Vector3(10f, 20f, 30f)));

        Node3D root = NodesBuilder.Build(dir.Path);
        TestScene.AddChild(root);

        // Hidden, but built: the labels exist and the console switch is what shows them, so turning them
        // on costs nothing and reveals the same set the level shipped.
        Assert.False(root.Visible);
        Assert.Single(root.GetChildren());
        Assert.True(root.IsInGroup(SceneGroups.Locations));

        root.QueueFree();
    }

    [Test]
    public void EachNamedPlaceGetsALabelAboveIt()
    {
        using var dir = new TempDir();
        dir.WriteNodes(("Belfast", new Vector3(10f, 20f, 30f)), ("Summerside", new Vector3(-40f, 5f, 60f)));

        Node3D root = NodesBuilder.Build(dir.Path);
        TestScene.AddChild(root);

        var labels = new List<Label3D>();
        foreach (Node child in root.GetChildren())
            if (child is Label3D label)
                labels.Add(label);

        Assert.Equal(2, labels.Count);
        Assert.Contains(labels, l => l.Text == "Belfast");
        Assert.Contains(labels, l => l.Text == "Summerside");

        // Lifted clear of most buildings and trees, and carrying the node's own position otherwise.
        Label3D belfast = labels.Find(l => l.Text == "Belfast")!;
        Assert.Equal(10f, belfast.Position.X);
        Assert.True(belfast.Position.Y > 20f, "the label was not raised above its node");
        // Unity -> Godot negates Z, which core's reader does; the label must not undo it.
        Assert.Equal(-30f, belfast.Position.Z);

        root.QueueFree();
    }

    // The three settings that make a place name legible rather than decorative. Any of them reverting
    // leaves a label that is still in the scene and still says the right word.
    [Test]
    public void TheLabelsAreReadableFromAnywhereOnTheIsland()
    {
        using var dir = new TempDir();
        dir.WriteNodes(("Alberton", new Vector3(0f, 0f, 0f)));

        Node3D root = NodesBuilder.Build(dir.Path);
        TestScene.AddChild(root);
        var label = (Label3D)root.GetChild(0);

        // Faces the camera from any angle.
        Assert.Equal(BaseMaterial3D.BillboardModeEnum.Enabled, label.Billboard);
        // Constant on-screen size, so a town name does not shrink to nothing across the island.
        Assert.True(label.FixedSize);
        // Readable through hills and buildings, like an on-map marker.
        Assert.True(label.NoDepthTest);
        // Outlined, or a pale name over a pale sky is unreadable.
        Assert.True(label.OutlineSize > 0);

        root.QueueFree();
    }

    // A map with no Nodes.dat still builds — plenty of workshop maps name nothing, and an empty island is
    // not a broken one.
    [Test]
    public void AMapThatNamesNothingStillBuilds()
    {
        using var dir = new TempDir();

        Node3D root = NodesBuilder.Build(dir.Path);
        TestScene.AddChild(root);

        // The node's own name is deliberately not asserted: QueueFree is deferred, so an earlier test's
        // node is still in the tree and Godot renames this one to avoid the clash. What matters is that a
        // root came back at all and that it carries nothing.
        Assert.Equal(0, root.GetChildCount());

        root.QueueFree();
    }

    // --- helpers -------------------------------------------------------------------------------------

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "unturned-godot-nodes-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        // A Nodes.dat carrying only LOCATION nodes, in the real layout (version 9: every optional field
        // present). The reader is exercised for real in core's LevelNodesTests; this only has to produce
        // something it accepts.
        public void WriteNodes(params (string Name, Vector3 Position)[] locations)
        {
            var bytes = new List<byte> { 9, (byte)locations.Length };
            foreach ((string name, Vector3 position) in locations)
            {
                bytes.AddRange(BitConverter.GetBytes(position.X));
                bytes.AddRange(BitConverter.GetBytes(position.Y));
                bytes.AddRange(BitConverter.GetBytes(position.Z));
                bytes.Add(0); // ENodeType.LOCATION
                byte[] utf8 = Encoding.UTF8.GetBytes(name);
                bytes.Add((byte)utf8.Length);
                bytes.AddRange(utf8);
            }
            File.WriteAllBytes(System.IO.Path.Combine(Path, "Nodes.dat"), bytes.ToArray());
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }
}
