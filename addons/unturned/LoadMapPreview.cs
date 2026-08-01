#if TOOLS
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.EditorTools;

// One-shot version of the dock's "Load preview", for when you just want the world in front of you: open
// this file and hit Ctrl+Shift+X (File > Run), no plugin to enable.
//
// The map is MAP=<folder> if set, otherwise whatever the main menu last remembered, otherwise PEI. A cold
// cache makes this block the editor for as long as the extraction takes — use the dock's "Warm cache"
// button for that, it runs in the background.
[Tool]
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class LoadMapPreview : EditorScript
{
    private const string DefaultMap = "PEI";
    private const string MenuConfigPath = "user://menu.cfg";

    // async void: _Run returns as soon as the first slice yields, and the rest continues on the editor's
    // own loop — which is the whole point, the editor keeps redrawing while the map builds.
    public override async void _Run()
    {
        if (WorldPreview.FindInstall() is not { } install)
        {
            GD.PrintErr("[preview] Unturned install not found. Set UNTURNED_PATH.");
            return;
        }

        if (EditorInterface.Singleton.GetEditedSceneRoot() is not { } root)
        {
            GD.PrintErr("[preview] No scene is open — the preview is added to the edited scene.");
            return;
        }

        string mapName = ResolveMapName();
        GD.Print($"[preview] Building {mapName} from {install}…");
        // Dock defaults (objects, no foliage, no shadows): this one-shot path has no checkboxes.
        var report = new List<string>();
        // An EditorScript is not a Node, so the staged build yields on the editor's own base control.
        Node3D preview = await WorldPreview.BuildAsync(install, mapName, WorldPreview.PreviewOptions.Default,
            EditorInterface.Singleton.GetBaseControl(), status => GD.Print("[preview] " + status), report);
        // The staged build yields between slices; the scene can be closed in the meantime.
        if (!GodotObject.IsInstanceValid(root))
        {
            preview.Free();
            GD.Print("[preview] scene closed while building; preview discarded.");
            return;
        }
        WorldPreview.Attach(root, preview);

        foreach (string line in report)
            GD.Print("[preview] " + line);
        GD.Print($"[preview] Added under {root.Name}/{WorldPreview.RootName}. " +
            "It is not owned by the scene, so saving will not write it into the .tscn.");
    }

    private static string ResolveMapName()
    {
        if (OS.GetEnvironment("MAP") is { Length: > 0 } fromEnv)
            return fromEnv;

        var config = new ConfigFile();
        if (config.Load(MenuConfigPath) == Error.Ok &&
            config.GetValue("menu", "map", "").AsString() is { Length: > 0 } remembered)
            return remembered;

        return DefaultMap;
    }
}
#endif
