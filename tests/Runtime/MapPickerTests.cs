using System.Collections.Generic;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The map browser.
//
// The subtlety worth holding is identity. Folder names are NOT unique across Steam workshop items, so two
// installed maps can both be called "Ruins" — which means a selection has to be carried by something that
// really distinguishes them, or a player picks one and gets the other.
//
// Everything else here is about the selection surviving: it must start on something (a browser with no
// current map has nothing to Play), and it must follow what the player clicks.
public class MapPickerTests : TestClass
{
    public MapPickerTests(Node testScene) : base(testScene) { }

    // Something is always selected, so Play always has a map. A browser that opened with nothing chosen
    // would need the player to discover that before the button did anything.
    [Test]
    public async Task SomethingIsAlwaysSelected()
    {
        MapPicker picker = MapPicker.Create(Maps(), initialSelection: null);
        TestScene.AddChild(picker);
        await NextFrame();

        Assert.NotNull(picker.Selected);
        picker.QueueFree();
    }

    // An initial selection is honoured — this is what a --map argument and the last-played map both come
    // through as.
    [Test]
    public async Task AnInitialSelectionIsHonoured()
    {
        MapPicker picker = MapPicker.Create(Maps(), initialSelection: "Washington");
        TestScene.AddChild(picker);
        await NextFrame();

        Assert.Equal("Washington", picker.Selected!.FolderName);
        picker.QueueFree();
    }

    // An initial selection nobody has falls back to something real rather than leaving nothing chosen. A
    // stale --map from a shell, or a workshop map since unsubscribed, both arrive here.
    [Test]
    public async Task AnUnknownInitialSelectionFallsBackToSomethingReal()
    {
        MapPicker picker = MapPicker.Create(Maps(), initialSelection: "NoSuchMap");
        TestScene.AddChild(picker);
        await NextFrame();

        Assert.NotNull(picker.Selected);
        Assert.Contains(Maps(), m => m.FolderName == picker.Selected!.FolderName);
        picker.QueueFree();
    }

    // Two maps with the SAME folder name are both selectable, and selecting one gives that one. Workshop
    // folder names are not unique, so identity cannot rest on them — a player picking the second "Ruins"
    // must not be handed the first.
    [Test]
    public async Task TwoMapsSharingAFolderNameStayDistinct()
    {
        var maps = new List<MapEntry>
        {
            new() { FolderName = "Ruins", Path = "/maps/a/Ruins", DisplayName = "Ruins" },
            new() { FolderName = "Ruins", Path = "/maps/b/Ruins", DisplayName = "Ruins" },
        };

        MapPicker picker = MapPicker.Create(maps, initialSelection: null);
        TestScene.AddChild(picker);
        await NextFrame();

        Assert.NotNull(picker.Selected);
        // Whichever was chosen, it is one of the two ACTUAL entries rather than a merge of them: the
        // paths differ, and that is the only thing that tells them apart.
        Assert.Contains(maps, m => ReferenceEquals(m, picker.Selected));

        picker.QueueFree();
    }

    // An empty catalogue is survivable. A fresh install with no maps found is not a crash — the menu
    // still has to draw so the player can read why.
    [Test]
    public async Task AnEmptyCatalogueIsSurvivable()
    {
        MapPicker picker = MapPicker.Create(new List<MapEntry>(), initialSelection: null);
        TestScene.AddChild(picker);
        await NextFrame();

        Assert.Null(picker.Selected);
        picker.QueueFree();
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static List<MapEntry> Maps() => new()
    {
        new() { FolderName = "PEI", Path = "/maps/PEI", DisplayName = "PEI", Source = MapSource.Official },
        new()
        {
            FolderName = "Washington",
            Path = "/maps/Washington",
            DisplayName = "Washington",
            Source = MapSource.Official,
        },
        new()
        {
            FolderName = "Ruins",
            Path = "/workshop/1234/Ruins",
            DisplayName = "Ruins",
            Source = MapSource.Workshop,
        },
    };

    private SignalAwaiter NextFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
}
