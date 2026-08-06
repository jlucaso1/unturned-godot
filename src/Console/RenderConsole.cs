using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.DevConsole;

namespace UnturnedGodot;

// Every knob the in-game console offers, and what each one actually moves.
//
// The point of this file is measurement, not options. "Parity first, then performance" only works if the
// second half can be measured on the world the first half produced, and the honest way to learn what a
// thing costs is to stop drawing it in a running frame and watch the same HUD that was there a second
// ago. Everything here is therefore reversible, immediate, and rendering-only: no toggle rebuilds the
// world, none of them touches collision, navigation, audio or the server's simulation, so the frame
// before and the frame after differ in exactly one submission.
//
// The names are dotted and read `subject[.part].property`:
//
//     foliage.enabled 0                  — the grass, flowers and pebbles
//     objects.trees.enabled 0            — Unturned's RESOURCE family: trees, rocks, bushes
//     sun.shadows.distance 32            — how far the shadow pass re-rasterizes the world
//     r.scale 0.5                        — render 3D at half resolution, UI untouched
//
// A property is always the last segment and always the same word for the same idea (`enabled` for every
// switch, `distance`/`range`/`size`/`scale` for the measurements), so a name can be guessed rather than
// looked up. Subjects are the things a frame is made of rather than the classes that build them: what a
// person watching a frame time wants to switch off is "the trees", and they should not have to know that
// trees are placements of a Resource asset batched into a MultiMesh to find that out.
//
// Bindings resolve their target through the scene-tree groups (see SceneGroups) every time they run,
// rather than holding a node: the world is torn down and rebuilt on every map load, so a captured
// reference would be pointing at a freed node by the second map. A binding that finds nothing returns a
// notice — the value is still remembered, and ConsoleRegistry.Reapply pushes it at the next world.
public static class RenderConsole
{
    private static ConsoleRegistry? Shared;
    private static bool StartupTaken;

    // How far past the frame cap's period still counts as hitting it. The limiter lands a fraction of a
    // millisecond either side of its target, so an exact comparison would call a capped frame uncapped on
    // every one that overshot slightly.
    private const double CapSlackMs = 0.5d;

    private static Node? HostNode;

    // Whichever node the console currently speaks through — the overlay in a normal session, the
    // benchmark's own context node in a run that has no overlay to open. Bindings resolve the world
    // from it, so it must be a node that is IN the tree the world was built into.
    //
    // Setting it also starts the frame clock, which is why this is not an auto-property: `perf` measures
    // frames from a node of its own, and the benchmark host is the one that most needs it.
    public static Node? Host
    {
        get => HostNode;
        set
        {
            HostNode = value;
            if (value != null)
                FrameClock.EnsureTicking(value);
        }
    }

    // Built once per process, so a configuration survives the menu and the next map load. See
    // ConsoleVariable for why the values live here rather than in the nodes they drive.
    public static ConsoleRegistry Console => Shared ??= Build(() => Host);

    // UG_CONSOLE, handed to the first caller that is ready to run it and to nobody after that. Two very
    // different sessions want it — an interactive one, where the overlay runs it as though it had been
    // typed, and a benchmark tier, which has no overlay at all and reports through the log — and neither
    // may run it twice.
    public static bool TryTakeStartupLine(out string line)
    {
        line = OS.GetEnvironment("UG_CONSOLE");
        if (StartupTaken || line.Length == 0)
        {
            line = "";
            return false;
        }
        StartupTaken = true;
        return true;
    }

    public static ConsoleRegistry Build(Func<Node?> host)
    {
        var console = new ConsoleRegistry();
        // Defaults for the viewport-backed variables are read off the viewport this session actually
        // started with, not written down here a second time. UG_MESH_LOD_THRESHOLD is the case that
        // proves it: Main applies it before the console exists, so a hard-coded 2 would have `list`
        // reporting a threshold the frame is not using and `reset` moving the session to a value nobody
        // asked for. A run with no viewport yet (the unit-test harness) falls back to the shipped values.
        Viewport? viewport = host()?.GetViewport();

        console.Add(ConsoleVariable.Switch("terrain.enabled",
            "Draw the landscape tiles. Collision stays: the player still stands on ground they cannot see.",
            true, Visibility(host, SceneGroups.Terrain, "terrain")));
        console.Add(ConsoleVariable.Switch("terrain.splat.unpainted.enabled",
            "Sample the splat layers a pixel gives no weight to. Off (the default) skips them, which is "
            + "what makes the ground cost one or two texture fetches per pixel instead of the whole "
            + "painted set. On is the A/B control for that skip — the image is identical either way, so "
            + "the only difference between the two frames is the fetches.",
            false, SplatUnpainted(host)));

        console.Add(ConsoleVariable.Switch("objects.enabled",
            "Draw every placed object — buildings, props, trees and their authored lower levels.",
            true, Visibility(host, SceneGroups.Objects, "object world")));
        console.Add(ConsoleVariable.Switch("objects.small.enabled",
            "Draw SMALL objects: the clutter Unturned strips the collider from (signs, cans, litter).",
            true, Category(host, EObjectType.Small, "SMALL")));
        console.Add(ConsoleVariable.Switch("objects.medium.enabled",
            "Draw MEDIUM objects: furniture, fences, gravestones — the things a player walks around.",
            true, Category(host, EObjectType.Medium, "MEDIUM")));
        console.Add(ConsoleVariable.Switch("objects.large.enabled",
            "Draw LARGE objects: buildings and the rest of the world's architecture.",
            true, Category(host, EObjectType.Large, "LARGE")));
        console.Add(ConsoleVariable.Switch("objects.trees.enabled",
            "Draw the RESOURCE family — trees, rocks and bushes. The dominant submission on wooded maps.",
            true, Category(host, EObjectType.Resource, "RESOURCE")));
        console.Add(ConsoleVariable.Switch("objects.shadows.enabled",
            "Let placed objects cast into the shadow pass. Off keeps them lit and drawn, but out of it.",
            true, ObjectShadows(host)));

        console.Add(ConsoleVariable.Switch("foliage.enabled",
            "Draw the Foliage.blob grass, flowers and pebbles (~667k instances on PEI, 7.2M on Germany).",
            true, Visibility(host, SceneGroups.Foliage, "foliage")));
        console.Add(ConsoleVariable.Number("foliage.range",
            "Foliage draw distance as a fraction of the built range. Streaming is unchanged: this is the "
            + "cost of DRAWING it, isolated from the cost of having it resident.",
            1f, 0.01f, 1f, FoliageRange(host)));

        console.Add(ConsoleVariable.Switch("roads.enabled",
            "Draw the lofted road and river meshes.", true,
            Visibility(host, SceneGroups.Roads, "road network")));
        console.Add(ConsoleVariable.Switch("water.enabled",
            "Draw the sea plane. Transparent and map-wide, so it is submitted from almost everywhere.",
            true, Visibility(host, SceneGroups.Water, "water")));
        console.Add(ConsoleVariable.Switch("vehicles.enabled",
            "Draw the vehicles the map's spawn tables placed.", true,
            Visibility(host, SceneGroups.Vehicles, "vehicle set")));
        console.Add(ConsoleVariable.Switch("locations.enabled",
            "Float the map's place names (towns, landmarks) over the world. Off by default: the game "
            + "names a place when you reach it rather than hanging billboards over the island, so this "
            + "is a view of the level's own data — useful while working on a map, not while playing one.",
            false, Visibility(host, SceneGroups.Locations, "location set")));
        console.Add(ConsoleVariable.Switch("npcs.enabled",
            "Draw the NPC characters the map places. One skinned rig each — nothing batches them.",
            true, Visibility(host, SceneGroups.Npcs, "NPC set")));
        console.Add(ConsoleVariable.Switch("zombies.enabled",
            "Draw the zombie avatars. The simulation keeps running: they still hunt, hit and die.",
            true, Visibility(host, SceneGroups.Zombies, "zombie view")));
        console.Add(ConsoleVariable.Switch("players.enabled",
            "Draw the other players' characters. Their movement is still received and simulated.",
            true, Visibility(host, SceneGroups.RemotePlayers, "remote player view")));

        console.Add(ConsoleVariable.Switch("sun.enabled",
            "The directional light itself. Off leaves the world lit by ambient alone.",
            true, Lighting(host, (cycle, value) => cycle.Sun.Visible = value.AsBool)));
        console.Add(ConsoleVariable.Switch("sun.shadows.enabled",
            "The shadow pass. It re-rasterizes every caster in range, so it is often the largest single "
            + "block of geometry a frame submits.",
            true, Lighting(host, (cycle, value) => cycle.Sun.ShadowEnabled = value.AsBool)));
        console.Add(ConsoleVariable.Number("sun.shadows.distance",
            "How far the directional shadow cascades reach, in metres. Halving it halves what the shadow "
            + "pass has to draw; the world keeps its lighting either way.",
            64f, 1f, 1024f, Lighting(host,
                (cycle, value) => cycle.Sun.DirectionalShadowMaxDistance = value.AsFloat)));
        // A choice rather than a 1..4 range: the engine has no three-cascade mode, so a 3 the console
        // remembered would be a number the renderer never ran.
        console.Add(ConsoleVariable.Choice("sun.shadows.cascades",
            "How many times the shadow distance is split: 1, 2 or 4. Every split is another pass over the "
            + "casters in its slice, so this is the shadow pass's geometry cost. Fewer splits spread the "
            + "same map over more ground, and that IS visible up close — at 64 m over one 2048 cascade a "
            + "texel is ~62 mm everywhere, against the ~3 mm a screen pixel covers at 5 m.",
            2, new[] { 1, 2, 4 },
            Lighting(host, (cycle, value) => cycle.Sun.DirectionalShadowMode = value.AsInt switch
            {
                1 => DirectionalLight3D.ShadowMode.Orthogonal,
                4 => DirectionalLight3D.ShadowMode.Parallel4Splits,
                _ => DirectionalLight3D.ShadowMode.Parallel2Splits,
            })));
        console.Add(ConsoleVariable.Switch("sun.shadows.blend",
            "Cross-fade the seam between cascades. On (the default here, though not Godot's) every pixel "
            + "in the blend band takes a SECOND shadow lookup; off makes the band one lookup again and "
            + "leaves a visible line where the cascades meet — at the split, ~16 m out.",
            true, Lighting(host,
                (cycle, value) => cycle.Sun.DirectionalShadowBlendSplits = value.AsBool)));

        console.Add(ConsoleVariable.Switch("env.sky.enabled",
            "Draw the ported Unturned skybox. Off replaces it with a flat clear colour.",
            true, Lighting(host, (cycle, value) => cycle.WorldEnvironment.BackgroundMode = value.AsBool
                ? Godot.Environment.BGMode.Sky
                : Godot.Environment.BGMode.Color)));
        console.Add(ConsoleVariable.Switch("env.fog.enabled",
            "The distance haze the map's own Lighting.dat drives. Its density still follows the cycle.",
            true, Lighting(host, (cycle, value) => cycle.WorldEnvironment.FogEnabled = value.AsBool)));
        console.Add(ConsoleVariable.Switch("env.volumetric.enabled",
            "Volumetric fog. Off by default here — switch it on to price it before adopting it.",
            false, Lighting(host,
                (cycle, value) => cycle.WorldEnvironment.VolumetricFogEnabled = value.AsBool)));
        console.Add(ConsoleVariable.Switch("env.ssao.enabled",
            "Screen-space ambient occlusion. Off by default; this is what it would cost.",
            false, Lighting(host, (cycle, value) => cycle.WorldEnvironment.SsaoEnabled = value.AsBool)));
        console.Add(ConsoleVariable.Switch("env.ssil.enabled",
            "Screen-space indirect lighting. Off by default; this is what it would cost.",
            false, Lighting(host, (cycle, value) => cycle.WorldEnvironment.SsilEnabled = value.AsBool)));
        console.Add(ConsoleVariable.Switch("env.glow.enabled",
            "Bloom. Off by default; this is what it would cost.",
            false, Lighting(host, (cycle, value) => cycle.WorldEnvironment.GlowEnabled = value.AsBool)));

        console.Add(ConsoleVariable.Number("r.scale",
            "3D resolution scale. The UI and this console stay sharp, so 0.5 answers 'am I pixel-bound' "
            + "without changing anything else about the frame.",
            viewport?.Scaling3DScale ?? 1f, 0.25f, 2f,
            ViewportSetting(host, (target, value) => target.Scaling3DScale = value.AsFloat)));
        console.Add(ConsoleVariable.Whole("r.msaa",
            "3D antialiasing: 0 off, 1 2x, 2 4x, 3 8x.", (int)(viewport?.Msaa3D ?? Viewport.Msaa.Disabled),
            0, 3, ViewportSetting(host, (target, value) => target.Msaa3D = (Viewport.Msaa)value.AsInt)));
        console.Add(ConsoleVariable.Switch("r.taa.enabled",
            "Temporal antialiasing.", viewport?.UseTaa ?? false,
            ViewportSetting(host, (target, value) => target.UseTaa = value.AsBool)));
        console.Add(ConsoleVariable.Switch("r.occlusion.enabled",
            "Occlusion culling against the terrain occluders. Off is the A/B control for what they buy.",
            viewport?.UseOcclusionCulling ?? true,
            ViewportSetting(host, (target, value) => target.UseOcclusionCulling = value.AsBool)));
        console.Add(ConsoleVariable.Number("r.lod.threshold",
            "Godot's automatic mesh LOD, in pixels of screen-space error. 0 disables it, which is the "
            + "only way to see what the meshoptimizer chain is worth. Matches UG_MESH_LOD_THRESHOLD.",
            viewport?.MeshLodThreshold ?? 2f, 0f, 1024f,
            ViewportSetting(host, (target, value) => target.MeshLodThreshold = value.AsFloat)));
        console.Add(ConsoleVariable.Whole("r.shadow.atlas",
            "Positional (point/spot) shadow atlas edge, in texels.",
            viewport?.PositionalShadowAtlasSize ?? 1024, 128, 8192,
            ViewportSetting(host, (target, value) => target.PositionalShadowAtlasSize = value.AsInt)));
        console.Add(ConsoleVariable.Whole("r.shadow.directional",
            "The sun's shadow map edge, in texels — cleared and written every frame, so on a machine "
            + "whose GPU shares system memory it is bandwidth before it is anything else. Read it against "
            + "`sun.shadows.distance`: 2 cascades over 64 m at 4096 spend a texel per 4 mm of near ground "
            + "and per 16 mm of far, where one screen pixel at 64 m covers ~40 mm. Halving the edge "
            + "quarters the bytes and is still finer than the display.",
            DirectionalShadowSize(), 256, 16384, value =>
            {
                RenderingServer.DirectionalShadowAtlasSetSize(value.AsInt, DirectionalShadow16Bits());
                return null;
            }));
        console.Add(ConsoleVariable.Whole("r.shadow.filter",
            "Soft shadow filter quality: 0 hard (one tap), 1-2 low, 3 medium, 4-5 high. Every step up is "
            + "more taps per shadowed pixel. Unlike the two above, this one is visible — it is the "
            + "softness of the edge — so it is a trade, not a free saving.",
            SoftShadowFilterQuality(), 0, 5, value =>
            {
                RenderingServer.DirectionalSoftShadowFilterSetQuality(
                    (RenderingServer.ShadowQuality)value.AsInt);
                return null;
            }));
        console.Add(ConsoleVariable.Whole("r.debug",
            "Viewport debug draw: 0 normal, 1 unshaded, 2 lighting, 3 overdraw, 4 wireframe. Overdraw and "
            + "wireframe are how you find WHICH geometry is expensive rather than how much there is.",
            (int)(viewport?.DebugDraw ?? Viewport.DebugDrawEnum.Disabled), 0, 4,
            ViewportSetting(host, (target, value) => target.DebugDraw = (Viewport.DebugDrawEnum)value.AsInt)));
        console.Add(ConsoleVariable.Switch("r.vsync.enabled",
            "Present in step with the display. Off is required for a frame time that means anything: with "
            + "it on, every measurement below the refresh rate reads as the refresh rate.",
            DisplayServer.WindowGetVsyncMode() != DisplayServer.VSyncMode.Disabled, value =>
            {
                DisplayServer.WindowSetVsyncMode(value.AsBool
                    ? DisplayServer.VSyncMode.Enabled
                    : DisplayServer.VSyncMode.Disabled);
                return null;
            }));
        console.Add(ConsoleVariable.Whole("r.fps.max",
            "Frame cap; 0 is uncapped. A cap is the other way to stop a GPU-bound measurement from "
            + "being a thermal one.",
            Math.Clamp(Engine.MaxFps, 0, 1000), 0, 1000, value =>
            {
                Engine.MaxFps = value.AsInt;
                return null;
            }));

        console.Add("perf", "The numbers the F3 HUD shows, into the scrollback — so a measurement is "
            + "recorded next to the command that produced it. The second line says where the frame goes: "
            + "its first number is frame minus cpu minus physics, and it is only a GPU reading when the "
            + "frame ran flat out — so it reads 'gpu wait' uncapped (0.00 means the CPU is the bottleneck "
            + "and removing GPU work will not move the frame) and 'idle' under vsync or r.fps.max, where "
            + "it is the limiter sleeping rather than the GPU. Godot reports the "
            + "monitors of the LAST COMPLETED frame, so `foo 0; perf` on one line prices the frame "
            + "BEFORE the change — put perf on its own line. 'fps' is the last SECOND averaged, so it and "
            + "'frame' describe different windows and lag each other while something is changing.",
            "perf", arguments => Snapshot(host, arguments));
        console.Add("copy", "Put the whole scrollback on the clipboard as plain text — the transcript a "
            + "bug report wants. Ctrl+C copies just what you have selected, when something is.",
            "copy", arguments => Copy(host, arguments));
        console.Add("clear", "Empty the scrollback. The log keeps running; only what is on screen goes.",
            "clear", arguments => Clear(host, arguments));
        console.Add("quit", "Leave the game, the same cooperative shutdown the pause menu's button runs.",
            "quit", arguments => Quit(host, arguments));

        return console;
    }

    // The renderer-global shadow settings have no node and no viewport to read back from — RenderingServer
    // takes them and does not return them — so their defaults come from where the engine itself took them.
    private static int DirectionalShadowSize() =>
        ProjectSettings.GetSetting("rendering/lights_and_shadows/directional_shadow/size", 4096).AsInt32();

    private static bool DirectionalShadow16Bits() =>
        ProjectSettings.GetSetting("rendering/lights_and_shadows/directional_shadow/16_bits", true).AsBool();

    private static int SoftShadowFilterQuality() => ProjectSettings.GetSetting(
        "rendering/lights_and_shadows/directional_shadow/soft_shadow_filter_quality", 2).AsInt32();

    // Resolved through a provider rather than a captured node, and re-resolved on every call. Both
    // halves matter: the registry is built once per process so a configuration survives a return to the
    // menu and the load of another map, and the nodes it drives do not survive that at all — every one
    // of them is freed and rebuilt. A binding that held a Node would be holding a freed one by the
    // second map.
    private static Node? Located(Func<Node?> host, string group)
    {
        Node? anchor = host();
        return anchor is { } node && node.IsInsideTree() ? node.GetTree().GetFirstNodeInGroup(group) : null;
    }

    // Every node in the group, not just the first. A subject is not always one node: what the objects
    // builder DRAWS is its batch renderer plus the placeholder boxes for assets it could not extract,
    // and hiding one of the two would leave a measurement quietly counting the other.
    private static Godot.Collections.Array<Node> AllLocated(Func<Node?> host, string group) =>
        host() is { } anchor && anchor.IsInsideTree()
            ? anchor.GetTree().GetNodesInGroup(group)
            : new Godot.Collections.Array<Node>();

    // Shows or hides one part of the built world. Hiding a root propagates through IsVisibleInTree, which
    // is what the RID renderers below it watch, so this reaches batches that are not nodes at all — and
    // reaches nothing that is not drawn: static bodies, ladder volumes and navigation are unaffected.
    private static ConsoleApply Visibility(Func<Node?> host, string group, string what) => value =>
    {
        int shown = 0;
        foreach (Node node in AllLocated(host, group))
        {
            if (node is not Node3D drawn)
                continue;
            drawn.Visible = value.AsBool;
            shown++;
        }
        return shown > 0
            ? null
            : $"No {what} is loaded right now; the value is kept and applied to the next world.";
    };

    // Reaches into the splat material of every landscape tile. Tiles whose layer textures could not be
    // resolved wear the flat fallback material instead and have no such parameter, so they are counted
    // apart: "nothing changed" on a flat-colour map is an answer, not a silent no-op.
    private static ConsoleApply SplatUnpainted(Func<Node?> host) => value =>
    {
        int splat = 0;
        int flat = 0;
        foreach (Node root in AllLocated(host, SceneGroups.Terrain))
        {
            foreach (Node child in root.GetChildren())
            {
                if (child is not MeshInstance3D { Mesh: { } mesh } || mesh.GetSurfaceCount() == 0)
                    continue;
                if (mesh.SurfaceGetMaterial(0) is ShaderMaterial material)
                {
                    material.SetShaderParameter("sample_unpainted", value.AsBool);
                    splat++;
                }
                else
                {
                    flat++;
                }
            }
        }
        if (splat > 0)
            return null;
        return flat > 0
            ? "This map's terrain wears the flat-colour fallback material, which has no splat layers to "
              + "skip; the value is kept and applied to the next world."
            : "No terrain is loaded right now; the value is kept and applied to the next world.";
    };

    private static ConsoleApply Category(Func<Node?> host, EObjectType category, string what) => value =>
    {
        if (Located(host, SceneGroups.ObjectBatches) is not MultiMeshRidRenderer renderer)
            return "No object batches are loaded right now; the value is kept and applied to the next world.";
        int batches = renderer.SetCategoryVisible(category, value.AsBool);
        return batches > 0 ? null : $"This map submits no {what} batches, so nothing changed.";
    };

    private static ConsoleApply ObjectShadows(Func<Node?> host) => value =>
    {
        if (Located(host, SceneGroups.ObjectBatches) is not MultiMeshRidRenderer renderer)
            return "No object batches are loaded right now; the value is kept and applied to the next world.";
        return renderer.SetShadowsEnabled(value.AsBool)
            ? null
            : "These batches were not built with one shadow setting, so there is nothing to restore them "
              + "to; the request was refused rather than guessed at.";
    };

    private static ConsoleApply FoliageRange(Func<Node?> host) => value =>
    {
        Node? node = Located(host, SceneGroups.Foliage);
        if (node == null)
            return "No foliage is loaded right now; the value is kept and applied to the next world.";
        if (node is not FoliageStreamingRenderer streaming)
        {
            return "This session's foliage is not the streamed renderer (UG_FOLIAGE_RESIDENCY=0 or "
                + "UG_NODE_MULTIMESH=1), and only that one can move its draw distance while running.";
        }
        streaming.SetRangeScale(value.AsFloat);
        return null;
    };

    private static ConsoleApply Lighting(Func<Node?> host, Action<DayNightController, ConsoleVariable> set) =>
        value =>
        {
            if (Located(host, SceneGroups.Lighting) is not DayNightController cycle)
                return "No lighting is loaded right now; the value is kept and applied to the next world.";
            set(cycle, value);
            return null;
        };

    private static ConsoleApply ViewportSetting(Func<Node?> host, Action<Viewport, ConsoleVariable> set) => value =>
    {
        if (host() is not { } anchor || anchor.GetViewport() is not { } viewport)
            return "This node is not in a viewport; the value is kept and applied when it is.";
        set(viewport, value);
        return null;
    };

    // The second line is the one that decides what to optimize next, and every hard part of it is in what
    // the leading number is allowed to be CALLED.
    //
    // Godot exposes no GPU-time monitor, so nothing here measures the GPU. What is computable is the time
    // in the frame that the engine did not report as CPU work: wall clock minus the idle step minus every
    // physics step that ran inside it. That remainder is only the CPU's wait on the GPU when it ran
    // flat out — rendering is submitted on the main thread and does not block, so it sits at ~0 while the
    // CPU is the bottleneck (even at full GPU utilisation) and opens up once the GPU is genuinely behind.
    //
    // Under vsync or a frame cap the same remainder is mostly the limiter sleeping, and calling that a GPU
    // wait points at the wrong bottleneck. The console owns both limiters, so it checks them and renames
    // the number instead of leaving the reader to remember. `0.00 ms gpu wait` uncapped is therefore an
    // ANSWER — shaving GPU work will not move this frame — while `idle (vsync)` is a non-answer, and the
    // two used to be indistinguishable. For real GPU frame time an external tool (MangoHud, radeontop,
    // PIX) is still the instrument; draw calls and primitives are the workload proxies here.
    //
    // The wall clock is FrameClock's monotonic interval rather than 1000/fps or a process delta, because
    // both of those are the wrong quantity in exactly the workflow this line exists for — see FrameClock.
    //
    // `fps` stays the one-second average, because that is what an fps readout means and a single frame's
    // reciprocal is too jittery to read. So `fps` and `frame` deliberately describe different windows and
    // will not agree during a change — the averaged one is the steady state, the frame is the last one.
    // Whether the present path made this frame wait. Enabled always does — a frame slower than the refresh
    // still ends on a refresh boundary, so the sleep is there either way. Adaptive only does while the
    // frame is keeping up; past the refresh period it tears rather than waits. Mailbox never does.
    private static bool Blocking(DisplayServer.VSyncMode mode, double frameMs)
    {
        double refreshHz = DisplayServer.ScreenGetRefreshRate();
        double refreshMs = refreshHz > 0d ? 1000d / refreshHz : 0d;
        return mode switch
        {
            DisplayServer.VSyncMode.Disabled or DisplayServer.VSyncMode.Mailbox => false,
            // An unknown refresh rate leaves nothing to compare, so the conservative reading stands.
            DisplayServer.VSyncMode.Adaptive =>
                refreshMs <= 0d || frameMs <= refreshMs + CapSlackMs,
            _ => true,
        };
    }

    private static IEnumerable<ConsoleLine> Snapshot(Func<Node?> host, IReadOnlyList<string> arguments)
    {
        double fps = Performance.GetMonitor(Performance.Monitor.TimeFps);
        double cpuMs = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000d;
        // The averaged frame is the fallback, not the reading: the clock has nothing yet before the second
        // rendered frame of a session, and a wrong-window number beats none at all only because the
        // alternative is a blank column.
        double frameMs = FrameClock.LastFrameMs > 0d ? FrameClock.LastFrameMs
            : fps > 0d ? 1000d / fps : 0d;
        double navigationMs = Performance.GetMonitor(Performance.Monitor.TimeNavigationProcess) * 1000d;
        // Physics comes out too. TimeProcess is the idle step ALONE — the physics step is a separate
        // monitor, printed on the next line — so a frame that ran physics leaves that work in the
        // remainder, where a heavier rigid-body load would read as the GPU falling behind.
        //
        // And it comes out once PER STEP, summed as the steps happened. Below the physics tick rate the
        // engine runs several steps between two rendered frames while the monitor prices one, so
        // subtracting a single sample at 30 fps against 60 Hz physics leaves half the physics bill behind
        // — in the remainder, under a GPU label, in exactly the physics-bound session least able to afford
        // the wrong diagnosis. Multiplying that sample by the step count would only trade the error for an
        // assumption that every step cost the same, which is least true on the frames that have several:
        // what makes a step expensive arrives in bursts. FrameClock adds up what each step reported.
        //
        // A measured 0 is kept as 0: above the physics tick rate most rendered frames run no physics step
        // at all, and billing them one would eat a real GPU wait out of the high-FPS frame where that is
        // the entire question. Only an unmeasured clock falls back to the monitor's single sample.
        int steps = FrameClock.HasMeasured ? FrameClock.LastPhysicsSteps : 1;
        double physicsMs = FrameClock.HasMeasured
            ? FrameClock.LastPhysicsMs
            : Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000d;
        double idleMs = Mathf.Max(0d, frameMs - cpuMs - physicsMs);

        // What the remainder MEANS depends on whether the frame was allowed to run flat out, and the
        // console knows because both limiters are its own variables. Under vsync the remainder is mostly
        // the deliberate sleep, and reading that as "the GPU is behind" is exactly backwards.
        //
        // A frame cap only distorts while it BINDS, which is the difference between the cap being set and
        // the cap being reached: a 120 cap over a 45 fps workload never sleeps, and suppressing there
        // would withhold the diagnosis from the slow frame that most needs it — while r.fps.max's own help
        // recommends a cap during measurement, so that case is the norm rather than the exception.
        //
        // Binding means the frame landed AT the cap's period, so the test is an upper bound. A lower one
        // would be satisfied by every frame slower than the cap, which is most of them — the limiter
        // cannot make a frame shorter than its period, so "not meaningfully longer than it" is the whole
        // condition.
        //
        // Not every vsync mode blocks, so the mode is classified rather than compared against Disabled.
        // Mailbox presents from a queue and imposes no refresh cap at all, and Adaptive is vsync only
        // while the frame keeps up — below the refresh it tears instead of waiting, which is the same
        // "flat out" the reading needs. Treating those two as blocking would hide the diagnosis on
        // precisely the slow frames they were selected to keep responsive.
        bool vsync = Blocking(DisplayServer.WindowGetVsyncMode(), frameMs);
        bool capBinding = Engine.MaxFps > 0 && frameMs <= (1000d / Engine.MaxFps) + CapSlackMs;
        string idleLabel = (vsync, capBinding) switch
        {
            (true, true) => "idle (vsync + cap, not a gpu reading)",
            (true, false) => "idle (vsync, not a gpu reading)",
            (false, true) => "idle (fps cap, not a gpu reading)",
            _ => "gpu wait (derived)",
        };
        double drawCalls = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        double primitives = Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame);
        double objects = Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame);
        double vramMb = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / (1024d * 1024d);
        double rssMb = Benchmark.ProcessMemory.RssBytes() / (1024d * 1024d);
        yield return ConsoleLine.Reply(string.Format(CultureInfo.InvariantCulture,
            "{0:0} fps   {1:0.00} ms frame   {2:0.00} ms cpu   {3:0} draw calls   {4:0} primitives   "
            + "{5:0} render objects   {6:0.0} MB rss",
            fps, frameMs, cpuMs, drawCalls, primitives, objects, rssMb));
        // The step count is printed only when it is not 1, because that is the case where the physics
        // number stops being the monitor's own reading and the reader deserves to know why.
        string physicsSteps = steps > 1 ? $" ({steps} steps)" : "";
        yield return ConsoleLine.Reply(string.Format(CultureInfo.InvariantCulture,
            "{0:0.00} ms {1}   {2:0.00} ms physics{3}   {4:0.00} ms navigation   {5:0.0} MB vram",
            idleMs, idleLabel, physicsMs, physicsSteps, navigationMs, vramMb));
    }

    private static IEnumerable<ConsoleLine> Copy(Func<Node?> host, IReadOnlyList<string> arguments)
    {
        if (host() is not ConsoleOverlay overlay)
        {
            yield return ConsoleLine.Failure("There is no scrollback to copy.");
            yield break;
        }
        yield return overlay.CopyScrollback()
            ? ConsoleLine.Reply("Scrollback copied to the clipboard.")
            : ConsoleLine.Failure("This session has no clipboard to copy to.");
    }

    private static IEnumerable<ConsoleLine> Clear(Func<Node?> host, IReadOnlyList<string> arguments)
    {
        if (host() is ConsoleOverlay overlay)
        {
            overlay.ClearScrollback();
            yield break;
        }
        yield return ConsoleLine.Failure("There is no scrollback to clear.");
    }

    private static IEnumerable<ConsoleLine> Quit(Func<Node?> host, IReadOnlyList<string> arguments)
    {
        if (host() is not { } anchor || !anchor.IsInsideTree())
        {
            yield return ConsoleLine.Failure("The console is not in a scene tree; there is nothing to quit.");
            yield break;
        }
        yield return ConsoleLine.Reply("Quitting.");
        AppShutdown.RequestQuit(anchor.GetTree());
    }
}
