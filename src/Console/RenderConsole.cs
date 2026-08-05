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
    public static ConsoleRegistry Build(Func<Node?> host)
    {
        var console = new ConsoleRegistry();

        console.Add(ConsoleVariable.Switch("terrain.enabled",
            "Draw the landscape tiles. Collision stays: the player still stands on ground they cannot see.",
            true, Visibility(host, SceneGroups.Terrain, "terrain")));

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
            1f, 0.25f, 2f, ViewportSetting(host, (viewport, value) => viewport.Scaling3DScale = value.AsFloat)));
        console.Add(ConsoleVariable.Whole("r.msaa",
            "3D antialiasing: 0 off, 1 2x, 2 4x, 3 8x.", 0, 0, 3,
            ViewportSetting(host, (viewport, value) => viewport.Msaa3D = (Viewport.Msaa)value.AsInt)));
        console.Add(ConsoleVariable.Switch("r.taa.enabled",
            "Temporal antialiasing.", false,
            ViewportSetting(host, (viewport, value) => viewport.UseTaa = value.AsBool)));
        console.Add(ConsoleVariable.Switch("r.occlusion.enabled",
            "Occlusion culling against the terrain occluders. Off is the A/B control for what they buy.",
            true, ViewportSetting(host, (viewport, value) => viewport.UseOcclusionCulling = value.AsBool)));
        console.Add(ConsoleVariable.Number("r.lod.threshold",
            "Godot's automatic mesh LOD, in pixels of screen-space error. 0 disables it, which is the "
            + "only way to see what the meshoptimizer chain is worth. Matches UG_MESH_LOD_THRESHOLD.",
            2f, 0f, 1024f,
            ViewportSetting(host, (viewport, value) => viewport.MeshLodThreshold = value.AsFloat)));
        console.Add(ConsoleVariable.Whole("r.shadow.atlas",
            "Positional (point/spot) shadow atlas edge, in texels.", 1024, 128, 8192,
            ViewportSetting(host, (viewport, value) => viewport.PositionalShadowAtlasSize = value.AsInt)));
        console.Add(ConsoleVariable.Whole("r.debug",
            "Viewport debug draw: 0 normal, 1 unshaded, 2 lighting, 3 overdraw, 4 wireframe. Overdraw and "
            + "wireframe are how you find WHICH geometry is expensive rather than how much there is.",
            0, 0, 4,
            ViewportSetting(host, (viewport, value) => viewport.DebugDraw = (Viewport.DebugDrawEnum)value.AsInt)));
        console.Add(ConsoleVariable.Switch("r.vsync.enabled",
            "Present in step with the display. Off is required for a frame time that means anything: with "
            + "it on, every measurement below the refresh rate reads as the refresh rate. The default "
            + "shown here is the shipped one; a benchmark run has already turned it off for itself.",
            true, value =>
            {
                DisplayServer.WindowSetVsyncMode(value.AsBool
                    ? DisplayServer.VSyncMode.Enabled
                    : DisplayServer.VSyncMode.Disabled);
                return null;
            }));
        console.Add(ConsoleVariable.Whole("r.fps.max",
            "Frame cap; 0 is uncapped. A cap is the other way to stop a GPU-bound measurement from "
            + "being a thermal one.",
            0, 0, 1000, value =>
            {
                Engine.MaxFps = value.AsInt;
                return null;
            }));

        console.Add("perf", "One line of the numbers the F3 HUD shows, into the scrollback — so a "
            + "measurement is recorded next to the command that produced it.", "perf", Snapshot);
        console.Add("clear", "Empty the scrollback. The log keeps running; only what is on screen goes.",
            "clear", arguments => Clear(host, arguments));
        console.Add("quit", "Leave the game, the same cooperative shutdown the pause menu's button runs.",
            "quit", arguments => Quit(host, arguments));

        return console;
    }

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

    // Shows or hides one part of the built world. Hiding a root propagates through IsVisibleInTree, which
    // is what the RID renderers below it watch, so this reaches batches that are not nodes at all — and
    // reaches nothing that is not drawn: static bodies, ladder volumes and navigation are unaffected.
    private static ConsoleApply Visibility(Func<Node?> host, string group, string what) => value =>
    {
        if (Located(host, group) is not Node3D node)
            return $"No {what} is loaded right now; the value is kept and applied to the next world.";
        node.Visible = value.AsBool;
        return null;
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

    private static IEnumerable<ConsoleLine> Snapshot(IReadOnlyList<string> arguments)
    {
        double fps = Performance.GetMonitor(Performance.Monitor.TimeFps);
        double frameMs = fps > 0d ? 1000d / fps : 0d;
        double cpuMs = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000d;
        double drawCalls = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        double primitives = Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame);
        double objects = Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame);
        double rssMb = Benchmark.ProcessMemory.RssBytes() / (1024d * 1024d);
        yield return ConsoleLine.Reply(string.Format(CultureInfo.InvariantCulture,
            "{0:0} fps   {1:0.00} ms frame   {2:0.00} ms cpu   {3:0} draw calls   {4:0} primitives   "
            + "{5:0} render objects   {6:0.0} MB rss",
            fps, frameMs, cpuMs, drawCalls, primitives, objects, rssMb));
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
