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
    private Button _navigation = null!;
    private CheckBox _navRim = null!;
    private CheckBox _navBeacons = null!;
    private CheckBox _navBounds = null!;
    private CheckBox _navXray = null!;
    private SpinBox _navLift = null!;

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
        _maps.ItemSelected += _ => OnMapSelected();
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
        BuildNavigationSection();

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

    private CheckBox AddCheck(string text, bool on, string tooltip, Container? parent = null)
    {
        var box = new CheckBox { Text = text, ButtonPressed = on, TooltipText = tooltip };
        (parent ?? (Container)this).AddChild(box);
        return box;
    }

    // The navigation overlay's own controls. Separate from the preview's, because it is built from
    // different data (Environment/*.dat alone — no masterbundle, no cache) and is quick enough to put
    // up on its own while the world stays empty.
    private void BuildNavigationSection()
    {
        AddChild(new Label { Text = "Navigation" });
        _navigation = AddButton("Show navmesh", OnToggleNavigation,
            "Draw this map's baked navmesh into the edited scene: walkable surface coloured by island, "
            + "the rim where it stops, and a beacon over every patch nothing can walk to. Reads only "
            + "the map's Environment folder, so it needs no warm cache and works on the maps whose "
            + "terrain this port cannot build. This is the navmesh as baked — a session additionally "
            + "reconciles it against collision.");

        var grid = new GridContainer { Columns = 2 };
        AddChild(grid);
        _navRim = AddCheck("Rim", true,
            "The outline of the walkable surface. A rim line in the middle of open ground is a hole.",
            grid);
        _navBeacons = AddCheck("Beacons", true,
            "A vertical marker over every island that is cut off from its flag's main surface — a "
            + "rooftop with no way up, a sealed cellar, a ledge. Drawn through terrain so they can be "
            + "spotted from the air.", grid);
        _navBounds = AddCheck("Bounds", false,
            "Yellow: the baked navmesh's own extent. Magenta: the same box expanded by 64 m, which is "
            + "where zombies may spawn. Magenta reaching well past the coloured surface is spawn "
            + "ground with no navmesh under it.", grid);
        _navXray = AddCheck("X-ray", false,
            "Draw the surface and rim through everything, so a navmesh inside a building can be read "
            + "from outside it.", grid);
        foreach (CheckBox box in new[] { _navRim, _navBeacons, _navBounds, _navXray })
            box.Toggled += _ => ApplyNavigationOptions();

        var lift = new HBoxContainer();
        AddChild(lift);
        lift.AddChild(new Label { Text = "Lift" });
        _navLift = new SpinBox
        {
            MinValue = 0,
            MaxValue = 5,
            Step = 0.05,
            Value = NavigationPreview.Options.Default.Lift,
            Suffix = "m",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "How far above the world to float the overlay. The baked mesh sits ON the "
                + "ground it describes, so without a little lift the two z-fight and the surface "
                + "shimmers as you move.",
        };
        _navLift.ValueChanged += _ => ApplyNavigationOptions();
        lift.AddChild(_navLift);
    }

    private NavigationPreview.Options NavigationOptions => new(
        Rim: _navRim.ButtonPressed,
        Beacons: _navBeacons.ButtonPressed,
        Bounds: _navBounds.ButtonPressed,
        XRay: _navXray.ButtonPressed,
        Lift: (float)_navLift.Value);

    // Every one of these is a property write on nodes that already exist, so the overlay reacts while
    // the camera keeps moving instead of being rebuilt under it.
    private void ApplyNavigationOptions()
    {
        if (EditorInterface.Singleton.GetEditedSceneRoot() is not { } root)
            return;
        if (NavigationPreview.Find(root) is { } overlay)
            NavigationPreview.Apply(overlay, NavigationOptions);
    }

    private async void OnToggleNavigation()
    {
        if (_unturnedPath is not { } install || Selected is not { } map || _busy)
            return;

        if (EditorInterface.Singleton.GetEditedSceneRoot() is not { } root)
        {
            Log("[color=orange]Open a scene first — the overlay is added to the edited scene.[/color]");
            return;
        }

        // Hides every overlay this dock put up, not only this tab's: the button is one toggle for one
        // dock, so leaving overlays standing in the scenes it is not looking at would make "Hide" mean
        // something different depending on which tab happened to be open.
        if (HideOverlays())
            return;

        SetBusy(true);
        Log($"Reading {map.DisplayName}'s navmesh…");
        var report = new List<string>();
        try
        {
            // The entry's own path, not a lookup by folder name: folder names are not unique across
            // workshop items, and resolving one picks the first match — which can be a different
            // workshop map from the one the dock is describing right above this button.
            Node3D overlay = await NavigationPreview.BuildAsync(map.Path, NavigationOptions, this,
                status => { if (Alive) _navigation.Text = status; }, report);

            // The staged build yields between flags, so the scene can close in the meantime — or be
            // SWITCHED, which validity alone does not catch: the scene the build started in is still
            // a live object while it stays open in another tab, so attaching to it would hide this
            // overlay in a scene the user has left and that nothing here will clear again.
            Node? edited = EditorInterface.Singleton.GetEditedSceneRoot();
            if (!Alive || !GodotObject.IsInstanceValid(root) || edited == null
                || edited.GetInstanceId() != root.GetInstanceId())
            {
                overlay.Free();
                return;
            }

            NavigationPreview.Attach(root, overlay);
            RememberScene(root);
            // The overlay's own checkboxes stay live while it builds, and their handler had nothing to
            // apply to until now, so anything toggled during the build would be silently dropped.
            NavigationPreview.Apply(overlay, NavigationOptions);
            foreach (string line in report)
                Log(line);
            WarnIfPreviewIsAnotherMap(root, map);
            Log($"[color=green]Overlay under {root.Name}/{NavigationPreview.RootName}.[/color]");
        }
        catch (Exception e)
        {
            Log($"[color=orange]Navmesh overlay failed: {e.GetType().Name}: {e.Message}[/color]");
        }
        finally
        {
            if (Alive)
            {
                SetBusy(false);
                UpdateNavigationButton(shown:
                    GodotObject.IsInstanceValid(root) && NavigationPreview.Find(root) != null);
            }
        }
    }

    // A world preview is expensive — minutes, on a cold cache — so selecting another map does not throw
    // it away. That leaves one way to be misled: the navmesh of map B drawn over the terrain of map A,
    // where every rim and every island lands on ground that has nothing to do with it. Saying so costs
    // a line and keeps the preview; silently clearing it would cost the build.
    private void WarnIfPreviewIsAnotherMap(Node root, MapEntry map)
    {
        if (root.GetNodeOrNull<Node3D>(WorldPreview.RootName) is not { } preview)
            return;
        string previewMap = preview.HasMeta(PreviewMapMeta)
            ? preview.GetMeta(PreviewMapMeta).AsString()
            : "";
        if (previewMap == map.SelectionKey)
            return;

        Log(previewMap.Length == 0
            ? "[color=orange]The world preview in this scene was not built by this dock, so the "
              + "navmesh may be drawn over another map's ground.[/color]"
            : $"[color=orange]The world preview in this scene is {previewMap}, not {map.DisplayName} — "
              + "this navmesh is drawn over another map's ground. Reload the preview to match.[/color]");
    }

    private const string PreviewMapMeta = "unturned_preview_map";

    private void UpdateNavigationButton(bool shown) =>
        _navigation.Text = shown ? "Hide navmesh" : "Show navmesh";

    private void ScanInstall()
    {
        // Invalidate a cache scan from the previous catalog even when discovery exits early.
        _cacheScanGeneration++;
        _maps.Clear();
        // Before either failure branch below, not after them. Both clear the map list and disable the
        // navigation button, so an overlay left standing by an install that has gone away — or by a
        // rescan that now finds no maps — is one the dock can no longer be used to take back down.
        DropOverlayFromAnotherMap();
        _unturnedPath = WorldPreview.FindInstall();
        if (_unturnedPath == null)
        {
            _install.Text = "Unturned install not found. Set UNTURNED_PATH and rescan.";
            SetButtonsEnabled(false);
            return;
        }

        _install.Text = _unturnedPath;
        _catalog = MapCatalog.Scan(_unturnedPath);
        // Pre-Landscape maps are listed and SELECTABLE, even though this port cannot build their
        // terrain. Their navmesh is read from Environment/ like any other map's, and refusing to let
        // them be picked at all would put the one thing that does work behind the one that does not.
        // SetButtonsEnabled turns off the buttons that genuinely need the terrain.
        foreach (MapEntry map in _catalog)
            _maps.AddItem(map.IsSupported ? map.DisplayName : $"{map.DisplayName} (no terrain)");

        if (_maps.ItemCount == 0)
        {
            _install.Text += "\nNo maps found under Maps/.";
            SetButtonsEnabled(false);
            return;
        }

        _maps.Selected = 0;
        SetButtonsEnabled(true);
        RefreshCacheState();
    }

    // Picking another map leaves the navmesh overlay describing the previous one, in the previous one's
    // coordinates. Dropping it says so rather than letting it be read as this map's.
    private void OnMapSelected()
    {
        RefreshCacheState();
        // The preview and warm buttons follow the selection too: a map this port cannot read terrain
        // for still has a navmesh worth looking at, so it is selectable, and only what actually needs
        // the terrain is turned off.
        SetButtonsEnabled(!_busy);
        DropOverlayFromAnotherMap();
    }

    // Every scene this dock has built something into. The editor keeps other open scenes alive in their
    // own tabs and GetEditedSceneRoot only ever names the current one, so clearing "the" overlay would
    // leave the previous map's navigation sitting in a tab one click away — with nothing on screen
    // saying which map it belongs to, and no way to take it down from a dock that has moved on.
    //
    // Kept across map changes rather than emptied with each sweep: this dock is the only thing that
    // knows a background tab was ever touched, and once that is forgotten nothing can find what it
    // left there. Pruned of dead scenes on every use, so it stays as short as the open tab list.
    private readonly List<Node> _touchedScenes = new();

    private void RememberScene(Node root)
    {
        _touchedScenes.RemoveAll(scene => !GodotObject.IsInstanceValid(scene));
        if (!_touchedScenes.Exists(scene => scene.GetInstanceId() == root.GetInstanceId()))
            _touchedScenes.Add(root);
    }

    // Every live scene this dock touched, plus the one in front of the user — which may hold something
    // this dock did not put there (a reloaded plugin, a second dock), and the button in front of them
    // should still take that down.
    private IEnumerable<Node> ScenesToSweep()
    {
        _touchedScenes.RemoveAll(scene => !GodotObject.IsInstanceValid(scene));
        foreach (Node scene in _touchedScenes)
            yield return scene;
        if (EditorInterface.Singleton.GetEditedSceneRoot() is { } edited
            && !_touchedScenes.Exists(scene => scene.GetInstanceId() == edited.GetInstanceId()))
            yield return edited;
    }

    // Takes down every navmesh overlay this dock is responsible for. Returns how many there were.
    private int ClearOverlays()
    {
        int cleared = 0;
        foreach (Node scene in ScenesToSweep())
            if (NavigationPreview.Clear(scene))
                cleared++;
        if (cleared > 0)
            UpdateNavigationButton(shown: false);
        return cleared;
    }

    // Everything this dock ever added, across every scene still open. The plugin calls this on its way
    // out: the dock owns the only record of which background tabs were touched, so freeing it first
    // strands whatever is in them — unowned nodes nothing will ever look for again.
    public void ClearEverything()
    {
        foreach (Node scene in ScenesToSweep())
        {
            NavigationPreview.Clear(scene);
            WorldPreview.Clear(scene);
        }
        _touchedScenes.Clear();
    }

    private bool HideOverlays()
    {
        int cleared = ClearOverlays();
        if (cleared > 0)
            Log(cleared == 1 ? "Navmesh overlay hidden." : $"{cleared} navmesh overlays hidden.");
        return cleared > 0;
    }

    private void DropOverlayFromAnotherMap()
    {
        int dropped = ClearOverlays();
        if (dropped == 0)
            return;
        Log(dropped == 1
            ? "Navmesh overlay dropped: it belonged to the previous map."
            : $"{dropped} navmesh overlays dropped across open scenes: they belonged to the "
              + "previous map.");
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
            (int missingMeshes, int missingTextures, int missingTerrainLayers, int needed) =
                await Task.Run(() => WorldPreview.CacheState(install, map.Path));
            result = missingMeshes == 0 && missingTextures == 0 && missingTerrainLayers == 0
                ? $"Cache ready ({needed} assets)."
                : $"{missingMeshes} of {needed} meshes not cached"
                    + (missingTextures > 0 ? $"; {missingTextures} textures pending" : "")
                    + (missingTerrainLayers > 0 ? $"; {missingTerrainLayers} terrain layers pending" : "")
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
            // SelectionKey, not FolderName: MapCatalog.Find matches the key before it falls back to
            // folder names, and folder names are not unique across workshop items — so a name resolved
            // the first matching entry, which need not be the one selected here. That also has to be
            // the identity stamped on the preview below, or the mismatch warning would compare the
            // entry the dock MEANT to build against terrain built from a different one and stay quiet.
            Node3D preview = await WorldPreview.BuildAsync(install, map.SelectionKey, options, this,
                status => { if (Alive) _preview.Text = status; }, report);

            // The staged build yields between slices, so the dock (and the scene) may be gone by now.
            if (!Alive || !GodotObject.IsInstanceValid(root))
            {
                preview.Free();
                return;
            }

            WorldPreview.Attach(root, preview);
            RememberScene(root);
            // Which map's ground this is, so the navmesh overlay can tell whether it is being drawn
            // over the terrain it describes. Metadata rather than a field: the preview outlives any
            // dock state (another dock, a reloaded plugin, the one-shot EditorScript).
            preview.SetMeta(PreviewMapMeta, map.SelectionKey);
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
            // Resolved by SelectionKey for the same reason the preview is: a duplicated workshop folder
            // name would otherwise warm a different item's cache than the one selected.
            string summary = await Task.Run(() => WorldPreview.WarmCache(install, map.SelectionKey));
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
        // Building the world and warming its mesh cache both need terrain this port can read; the
        // navmesh overlay does not, so it stays available on the maps those two cannot serve.
        bool terrain = Selected?.IsSupported != false;
        _preview.Disabled = !enabled || !terrain;
        _clear.Disabled = !enabled;
        _warm.Disabled = !enabled || !terrain;
        _refresh.Disabled = !enabled;
        _navigation.Disabled = !enabled;
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
