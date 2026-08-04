#if TOOLS
using Godot;

namespace UnturnedGodot.EditorTools;

// Registers the Unturned dock. The project builds its whole world from script at runtime, so without this
// the editor has nothing to show: Main.tscn is a bare Node3D and the only way to see a map is to press
// play. The dock builds one into the edited scene instead, so the editor's own camera can fly through it.
[Tool]
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class UnturnedEditorPlugin : EditorPlugin
{
    private MapPreviewDock? _dock;

    // AddControlToDock/RemoveControlFromDocks are marked obsolete in favour of AddDock(EditorDock), but
    // EditorDock does not exist in the GodotSharp 4.7 bindings this project builds against — the deprecation
    // notice is ahead of the C# API. Switch when the binding ships it.
#pragma warning disable CS0618
    public override void _EnterTree()
    {
        _dock = new MapPreviewDock();
        AddControlToDock(DockSlot.LeftUr, _dock);
    }

    public override void _ExitTree()
    {
        if (_dock == null)
            return;

        // Drop anything still in an open scene, so disabling the plugin does not leave hundreds of
        // orphaned mesh nodes behind. Through the dock rather than against the edited scene root: the
        // editor keeps every open tab alive, GetEditedSceneRoot only names the one in front of you, and
        // the dock holds the only record of which of the others were built into. Freeing it first would
        // strand whatever is in them — unowned nodes nothing will ever look for again.
        _dock.ClearEverything();

        // Put the editor's own viewport settings back. These live in EditorSettings, which is editor-wide
        // rather than project-scoped, so leaving them tuned follows the user into every other Godot
        // project on the machine — a 400 m/s freelook camera and a 2 km grid, with the backup file
        // orphaned and the only UI able to read it now gone. The dock's toggle was the sole way back.
        if (ViewportTuning.HasBackup() && ViewportTuning.Restore())
            GD.Print("[unturned] editor viewport settings restored on plugin exit.");

        RemoveControlFromDocks(_dock);
        _dock.Free();
        _dock = null;
    }
#pragma warning restore CS0618
}
#endif
