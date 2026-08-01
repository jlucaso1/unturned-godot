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

        // Drop any preview still in the edited scene, so disabling the plugin does not leave hundreds of
        // orphaned mesh nodes behind.
        if (EditorInterface.Singleton.GetEditedSceneRoot() is { } root)
            WorldPreview.Clear(root);

        RemoveControlFromDocks(_dock);
        _dock.Free();
        _dock = null;
    }
#pragma warning restore CS0618
}
#endif
