#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnturnedGodot.Data;

namespace UnturnedGodot.EditorTools;

// The "Unturned" editor dock: pick one of the installed maps, see whether its meshes are cached, warm that
// cache without launching the game, drop the built world into the edited scene, and lift the editor
// camera's pose back out as a SHOT_CAM the screenshot path can reproduce.
//
// Nothing here blocks the editor for long: the parsing runs on workers, the RenderingServer work is
// staged across frames, and the one genuinely long operation (extracting from the 1.4 GB masterbundle) is
// its own backgrounded button.
[Tool]
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class MapPreviewDock : VBoxContainer
{
    // How often the camera readout refreshes. The editor camera moves continuously while flying; a tenth
    // of a second reads as live without rebuilding the label every frame.
    private const double CameraPollSeconds = 0.1;

    private OptionButton _maps = null!;
    private Label _install = null!;
    private Label _cache = null!;
    private Label _camera = null!;
    private RichTextLabel _log = null!;
    private Button _preview = null!;
    private Button _clear = null!;
    private Button _warm = null!;
    private Button _refresh = null!;
    private Button _tune = null!;
    private CheckBox _objects = null!;
    private CheckBox _foliage = null!;
    private CheckBox _shadows = null!;

    private string? _unturnedPath;
    private IReadOnlyList<MapEntry> _catalog = Array.Empty<MapEntry>();
    private bool _busy;
    private double _sinceCameraPoll;
    private int _cacheScanGeneration;

    public override void _Ready()
    {
        Name = "Unturned";
        AddThemeConstantOverride("separation", 6);

        _install = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        AddChild(_install);

        _maps = new OptionButton { TooltipText = "Maps found in the Unturned install (official + workshop)" };
        _maps.ItemSelected += _ => RefreshCacheState();
        AddChild(_maps);

        _cache = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        AddChild(_cache);

        _objects = AddCheck("Objects", true,
            "Buildings, props and trees. The bulk of the build time — off gives you terrain, roads and "
            + "water almost instantly.");
        _foliage = AddCheck("Foliage", false,
            "Grass, flowers and pebbles: hundreds of thousands of instances. Cheap to build, expensive to "
            + "draw — leaving it off keeps the viewport smooth to fly through.");
        _shadows = AddCheck("Sun shadows", false,
            "A directional shadow spanning the whole map is the heaviest single thing in the frame.");

        _preview = AddButton("Load preview", OnLoadPreview,
            "Build the map into the edited scene, a slice at a time so the editor keeps redrawing. The "
            + "nodes are not owned by the scene, so they are never saved to the .tscn.");
        _clear = AddButton("Clear preview", OnClearPreview, "Remove the preview nodes from the edited scene.");
        _warm = AddButton("Warm cache", OnWarmCache,
            "Extract this map's meshes and textures from the masterbundle in the background, so a preview "
            + "(or a play session) starts instantly.");
        _tune = AddButton("Tune viewport for map scale", OnTuneViewport,
            "Scale the editor's freelook speed and far plane to this map's size. These are editor-wide "
            + "settings; the original values are backed up so this button can put them back.");
        _refresh = AddButton("Rescan maps", () => { ScanInstall(); }, "Re-read the install's map list.");

        AddChild(new HSeparator());
        _camera = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            TooltipText = "Live pose of the editor's 3D camera.",
        };
        AddChild(_camera);

        var cameraButtons = new HBoxContainer();
        AddChild(cameraButtons);
        AddButton("Copy SHOT_CAM", () => CopyCamera(commandLine: false),
            "Copy this view as SHOT_CAM=x,y,z,pitch,yaw — the same variable the screenshot path reads.",
            cameraButtons);
        AddButton("Copy screenshot cmd", () => CopyCamera(commandLine: true),
            "Copy a full headless screenshot command that reproduces this exact view. Paste it to an agent "
            + "to have it capture what you are looking at.",
            cameraButtons);

        _log = new RichTextLabel
        {
            CustomMinimumSize = new Vector2(0, 140),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            ScrollFollowing = true,
        };
        AddChild(_log);

        ScanInstall();
        UpdateTuneButton();
        SetProcess(true);
    }

    // Keeps the camera readout live. Cheap enough at a tenth of a second, and it is the only per-frame
    // work this dock does.
    public override void _Process(double delta)
    {
        _sinceCameraPoll += delta;
        if (_sinceCameraPoll < CameraPollSeconds)
            return;
        _sinceCameraPoll = 0;

        _camera.Text = EditorCamera() is { } camera
            ? $"cam {Fmt(camera.GlobalPosition)}\npitch {F(camera.GlobalRotationDegrees.X)}  " +
              $"yaw {F(camera.GlobalRotationDegrees.Y)}"
            : "cam — (open the 3D viewport)";
    }

    // The 3D editor viewport's camera. Reading its pose is fine; writing to it is not (the editor drives
    // that transform itself and overwrites anything set from outside).
    private static Camera3D? EditorCamera() =>
        EditorInterface.Singleton.GetEditorViewport3D(0)?.GetCamera3D();

    private Button AddButton(string text, Action pressed, string tooltip, Container? parent = null)
    {
        var button = new Button { Text = text, TooltipText = tooltip };
        button.Pressed += pressed;
        (parent ?? (Container)this).AddChild(button);
        return button;
    }

    private CheckBox AddCheck(string text, bool on, string tooltip)
    {
        var box = new CheckBox { Text = text, ButtonPressed = on, TooltipText = tooltip };
        AddChild(box);
        return box;
    }

    private void ScanInstall()
    {
        // Invalidate a cache scan from the previous catalog even when discovery exits early.
        _cacheScanGeneration++;
        _maps.Clear();
        _unturnedPath = WorldPreview.FindInstall();
        if (_unturnedPath == null)
        {
            _install.Text = "Unturned install not found. Set UNTURNED_PATH and rescan.";
            SetButtonsEnabled(false);
            return;
        }

        _install.Text = _unturnedPath;
        _catalog = MapCatalog.Scan(_unturnedPath);
        foreach (MapEntry map in _catalog)
        {
            _maps.AddItem(map.IsSupported ? map.DisplayName : $"{map.DisplayName} (no terrain)");
            _maps.SetItemDisabled(_maps.ItemCount - 1, !map.IsSupported);
        }

        if (_maps.ItemCount == 0)
        {
            _install.Text += "\nNo maps found under Maps/.";
            SetButtonsEnabled(false);
            return;
        }

        SetButtonsEnabled(true);
        _maps.Selected = 0;
        RefreshCacheState();
    }

    private MapEntry? Selected =>
        _maps.Selected >= 0 && _maps.Selected < _catalog.Count ? _catalog[_maps.Selected] : null;

    // The cache check reads the map's whole object list (100k+ entries on the bigger maps), so it runs on a
    // worker and reports back on the main thread.
    private async void RefreshCacheState()
    {
        if (_unturnedPath is not { } install || Selected is not { } map)
            return;
        int generation = ++_cacheScanGeneration;

        string scale = $"{map.TileCount} tiles, {map.SizeMetres:0} m across";
        _cache.Text = $"{scale}\nChecking cache…";

        string result;
        try
        {
            (int missingMeshes, int missingTextures, int needed) =
                await Task.Run(() => WorldPreview.CacheState(install, map.Path));
            result = missingMeshes == 0 && missingTextures == 0
                ? $"Cache ready ({needed} assets)."
                : $"{missingMeshes} of {needed} meshes not cached"
                    + (missingTextures > 0 ? $"; {missingTextures} textures pending" : "")
                    + " — warm the cache to complete the preview.";
        }
        catch (Exception e)
        {
            result = $"Cache state unavailable: {e.Message}";
        }

        // The dock can be torn down while the worker runs — closing the project, disabling the plugin, or
        // a headless export, which loads and drops the editor plugins in one pass. Touching the Label then
        // throws ObjectDisposedException out of the continuation, where nothing can catch it.
        if (!Alive || generation != _cacheScanGeneration || !ReferenceEquals(Selected, map))
            return;
        _cache.Text = scale + "\n" + result;
    }

    private async void OnLoadPreview()
    {
        if (_unturnedPath is not { } install || Selected is not { } map || _busy)
            return;

        if (EditorInterface.Singleton.GetEditedSceneRoot() is not { } root)
        {
            Log("[color=orange]Open a scene first — the preview is added to the edited scene.[/color]");
            return;
        }

        var options = new WorldPreview.PreviewOptions(
            Objects: _objects.ButtonPressed,
            Foliage: _foliage.ButtonPressed,
            Shadows: _shadows.ButtonPressed);

        SetBusy(true);
        Log($"Building {map.DisplayName}…");
        var report = new List<string>();
        try
        {
            // Built detached and attached only at the end: a half-finished world never appears in the
            // viewport, and a failure part-way leaves the scene as it was.
            Node3D preview = await WorldPreview.BuildAsync(install, map.FolderName, options, this,
                status => { if (Alive) _preview.Text = status; }, report);

            // The staged build yields between slices, so the dock (and the scene) may be gone by now.
            if (!Alive || !GodotObject.IsInstanceValid(root))
            {
                preview.Free();
                return;
            }

            WorldPreview.Attach(root, preview);
            foreach (string line in report)
                Log(line.StartsWith("FAIL") ? $"[color=orange]{line}[/color]" : line);
            Log($"[color=green]Preview under {root.Name}/{WorldPreview.RootName} " +
                "(not saved with the scene).[/color]");
        }
        catch (Exception e)
        {
            Log($"[color=orange]Preview failed: {e.GetType().Name}: {e.Message}[/color]");
        }
        finally
        {
            // Returning because the edited scene closed still lands here while the dock itself remains
            // alive. Restore its controls so the next scene can load a preview without restarting Godot.
            if (Alive)
            {
                _preview.Text = "Load preview";
                SetBusy(false);
                RefreshCacheState();
            }
        }
    }

    private void OnClearPreview()
    {
        if (EditorInterface.Singleton.GetEditedSceneRoot() is not { } root)
            return;
        Log(WorldPreview.Clear(root) ? "Preview cleared." : "No preview in this scene.");
    }

    private async void OnWarmCache()
    {
        if (_unturnedPath is not { } install || Selected is not { } map || _busy)
            return;

        SetBusy(true);
        _warm.Text = "Extracting…";
        Log($"Extracting {map.DisplayName} from the masterbundle (this can take a minute)…");
        try
        {
            string summary = await Task.Run(() => WorldPreview.WarmCache(install, map.FolderName));
            Log($"[color=green]{summary}[/color]");
        }
        catch (Exception e)
        {
            Log($"[color=orange]Extraction failed: {e.GetType().Name}: {e.Message}[/color]");
        }

        if (!Alive) // the extraction outlives the dock easily: it can run for a minute
            return;
        _warm.Text = "Warm cache";
        SetBusy(false);
        RefreshCacheState();
    }

    // One button that toggles: tune to the selected map's span, or put the editor's own values back.
    private void OnTuneViewport()
    {
        if (ViewportTuning.HasBackup())
        {
            ViewportTuning.Restore();
            Log("Editor viewport settings restored.");
        }
        else if (Selected is { } map)
        {
            Log(ViewportTuning.Apply(map.SizeMetres));
        }
        UpdateTuneButton();
    }

    // Puts the current view on the clipboard in the form the game already understands. SHOT_CAM is parsed
    // as "x,y,z,pitch,yaw" and applied to the free camera before the framebuffer is grabbed, so pasting
    // this into a headless run reproduces exactly what the editor is showing.
    private void CopyCamera(bool commandLine)
    {
        if (EditorCamera() is not { } camera)
        {
            Log("[color=orange]No 3D viewport camera to read.[/color]");
            return;
        }

        Vector3 p = camera.GlobalPosition;
        Vector3 r = camera.GlobalRotationDegrees;
        string shotCam = $"{F(p.X)},{F(p.Y)},{F(p.Z)},{F(r.X)},{F(r.Y)}";

        // The map folder is quoted: several official maps have a space in the name ("Alpha Valley"), which
        // would otherwise split into a second word and silently load the default map instead.
        string text = commandLine
            ? $"SCREENSHOT_PATH=/tmp/shot.png MAP=\"{Selected?.FolderName ?? "PEI"}\" " +
              $"SHOT_CAM={shotCam} \"$GODOT\" --audio-driver Dummy"
            : $"SHOT_CAM={shotCam}";

        DisplayServer.ClipboardSet(text);
        Log($"Copied: [code]{text}[/code]");
    }

    private void UpdateTuneButton() => _tune.Text = ViewportTuning.HasBackup()
        ? "Restore viewport defaults"
        : "Tune viewport for map scale";

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SetButtonsEnabled(!busy);
    }

    private void SetButtonsEnabled(bool enabled)
    {
        _preview.Disabled = !enabled;
        _clear.Disabled = !enabled;
        _warm.Disabled = !enabled;
        _refresh.Disabled = !enabled;
        _maps.Disabled = !enabled;
    }

    // Invariant formatting throughout: these strings are pasted into shell commands and env vars, where a
    // comma decimal separator would silently produce a different camera.
    private static string F(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Fmt(Vector3 v) => $"{F(v.X)}, {F(v.Y)}, {F(v.Z)}";

    // True while this dock is still a live node. Every await here can outlive the dock, so each one is
    // followed by this check before touching a control.
    private bool Alive => GodotObject.IsInstanceValid(this) && IsInsideTree();

    private void Log(string message)
    {
        if (Alive)
            _log.AppendText(message + "\n");
    }
}

#endif
