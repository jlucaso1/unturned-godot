using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Godot;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests;

// Guards a performance rule the compiler cannot see. InstancedStaticBody hands thousands of shapes to
// PhysicsServer3D; adding them while the body is already in a space makes the server re-register it with
// the broadphase on every single shape (godotengine/godot#24026). On a 105k-object map that turned an
// otherwise ~700 ms scene attach into a hang long enough to look like a freeze.
//
// The rule is one line of ordering, easy to undo by accident and invisible in any behavioural test the
// hermetic suite can run (a physics space needs a live Godot runtime), so it is checked in the source.
public class PhysicsBodyOrderTests
{
    // Hardcoded WASD has to ask for the PHYSICAL key, never the keycode. A keycode names the character
    // the key prints, which moves with the layout: on AZERTY the "W" key sits where QWERTY has Z, and
    // "A" where QWERTY has Q, so a free camera bound by keycode answers to a scattering of keys under
    // nobody's fingers. This is one of the few assertions in this file that is about an API choice
    // rather than a shape of text, so it survives reformatting.
    //
    // PlayerController is exempt on purpose: it reads the player's own Unturned binds, where the
    // character IS the question being asked.
    [Fact]
    public void FreeCamera_ReadsPhysicalKeys_SoItWorksOffQwerty()
    {
        if (FindRepositoryFile(Path.Combine("src", "UI", "FreeCamera.cs")) is not { } path)
            return; // running from a package without the sources next to it

        string source = File.ReadAllText(path);

        Assert.DoesNotContain("Input.IsKeyPressed(", source);
        foreach (string key in new[] { "Key.W", "Key.S", "Key.A", "Key.D", "Key.E", "Key.Q" })
            Assert.Contains($"Input.IsPhysicalKeyPressed({key})", source);
    }

    [Fact]
    public void InstancedStaticBody_AddsShapesBeforeJoiningTheSpace()
    {
        if (FindRepositoryFile(Path.Combine("src", "World", "InstancedStaticBody.cs")) is not { } path)
            return; // running from a package without the sources next to it

        string source = File.ReadAllText(path);
        int addShape = source.LastIndexOf("PhysicsServer3D.BodyAddShape", System.StringComparison.Ordinal);
        int setSpace = source.LastIndexOf("PhysicsServer3D.BodySetSpace", System.StringComparison.Ordinal);

        Assert.True(addShape >= 0, "BodyAddShape call not found");
        Assert.True(setSpace >= 0, "BodySetSpace call not found");
        Assert.True(setSpace > addShape,
            "BodySetSpace must come after the shapes are added: joining the space first makes every "
            + "body_add_shape re-register the body with the broadphase (godotengine/godot#24026).");
        int release = source.IndexOf("Placements = System.Array.Empty", setSpace,
            System.StringComparison.Ordinal);
        Assert.True(release > setSpace,
            "placement tuples must be released only after PhysicsServer has copied every transform");
    }

    // Attributing RSS between the game and the rendering driver needs one session that runs streaming,
    // navigation, physics and netcode with no driver at all. The flag must therefore skip the
    // synchronous-build-and-quit branch, force the auto-start (there is no menu to click without a
    // display), and still yield to a screenshot, which cannot be taken with nothing drawn.
    [Fact]
    public void HeadlessInteractiveRunsTheRealSessionAndStillYieldsToAScreenshot()
    {
        if (FindRepositoryFile(Path.Combine("src", "Main.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("EnvFlag.IsOn(OS.GetEnvironment(\"UG_HEADLESS_INTERACTIVE\"), whenUnset: false)", source);
        Assert.Contains("&& string.IsNullOrEmpty(shot)", source);
        Assert.Contains("if ((headless && !headlessInteractive) || !string.IsNullOrEmpty(shot))", source);
        Assert.Contains("bool autoStart = headlessInteractive", source);

        int flag = source.IndexOf("bool headlessInteractive", StringComparison.Ordinal);
        int build = source.IndexOf("if ((headless && !headlessInteractive)", StringComparison.Ordinal);
        int autoStart = source.IndexOf("bool autoStart = headlessInteractive", StringComparison.Ordinal);
        Assert.True(flag >= 0 && build > flag && autoStart > build);

        // The documented workflow is the script, and its runtime tier otherwise always takes a swapchain.
        // Handing the no-renderer control an Xvfb display would measure lavapipe and report it as the
        // game's memory, which is the confusion the flag exists to remove — so the script must branch too.
        if (FindRepositoryFile(Path.Combine("scripts", "run-benchmark.sh")) is not { } scriptPath)
            return;

        // A load failure has no reachable way out without a display, so it must fail the process rather
        // than present a Back button no one can press and leave a benchmark waiting on it forever.
        int failure = source.IndexOf("loading.Fail(", StringComparison.Ordinal);
        int headlessQuit = source.LastIndexOf("if (_headlessInteractive)", failure, StringComparison.Ordinal);
        Assert.True(headlessQuit >= 0 && headlessQuit < failure,
            "the headless route must quit before the on-screen failure path");
        Assert.Contains("GetTree().Quit(1);", source[headlessQuit..failure]);

        string script = File.ReadAllText(scriptPath);
        int runtimeTier = script.IndexOf("    runtime)", StringComparison.Ordinal);
        // The same screenshot precedence Main applies: a capture keeps the swapchain, or the run reaches
        // the quit-after-load branch and writes no PNG at all.
        int guard = script.IndexOf(
            "\"${UG_HEADLESS_INTERACTIVE:-}\" == \"1\" && -z \"${SCREENSHOT_PATH:-}\"", runtimeTier,
            StringComparison.Ordinal);
        int headlessLaunch = script.IndexOf("\"$godot\" --headless", guard, StringComparison.Ordinal);
        int windowed = script.IndexOf("run_windowed", guard, StringComparison.Ordinal);
        Assert.True(runtimeTier >= 0 && guard > runtimeTier, "the runtime tier must branch on the flag");
        Assert.True(headlessLaunch > guard && windowed > headlessLaunch,
            "the flag must launch --headless, and the windowed launch must stay on the other branch");
    }

    [Fact]
    public void IdlePhysicsFastPath_DoesNotSuppressMultiplayerFrames()
    {
        if (FindRepositoryFile(Path.Combine("src", "Player", "PlayerController.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        int integrationGuard = source.IndexOf("if (integrate)", System.StringComparison.Ordinal);
        int sendInput = source.IndexOf("Net.SendInput", System.StringComparison.Ordinal);
        int nextMethod = source.IndexOf("private bool UpdateStance", System.StringComparison.Ordinal);

        Assert.True(integrationGuard >= 0 && sendInput > integrationGuard && nextMethod > sendInput);
        int guardEnd = source.LastIndexOf('}', sendInput);
        Assert.True(guardEnd > integrationGuard,
            "the integration guard must close before Net.SendInput; idle clients still send keepalive/state frames");
        // Stronger structural check: the complete networking block occurs after the local `isOnFloor`
        // result, outside the guarded MoveAndSlide block.
        Assert.Contains("if (Net != null)", source[guardEnd..nextMethod]);
    }

    [Fact]
    public void RemotePlayersReuseTheAlreadyImportedLocalTemplate()
    {
        if (FindRepositoryFile(Path.Combine("src", "Main.cs")) is not { } mainPath
            || FindRepositoryFile(Path.Combine("src", "Net", "RemotePlayersView.cs")) is not { } viewPath)
            return;

        string main = File.ReadAllText(mainPath);
        string view = File.ReadAllText(viewPath);
        Assert.Contains("player.BodyModel", main);
        Assert.Contains("_template = localTemplate", view);
        Assert.Contains("_template ??= CharacterModel.Build", view); // safe standalone fallback remains
    }

    [Fact]
    public void NavigationGraph_IsBuiltOffTheMainThreadAfterReconciliation()
    {
        if (FindRepositoryFile(Path.Combine("src", "Net", "ZombieNavigation.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("await Task.Run(() =>", source);
        Assert.Contains("BakedNavGraph.Build(_flags, _unreachable, _portalProbe,", source);
        Assert.Contains("validateIndexedPortals: true", source);
        Assert.Contains("await PublishAsync(fingerprint, cachePath + \".csr\")", source);
        Assert.Contains("await PublishAsync(fingerprint, cachePath == null ? null : cachePath + \".csr\")", source);
        Assert.Contains("BakedNavGraph.TryReadFile", source);
        Assert.Contains("built.Write(output, fingerprint)", source);
        Assert.Contains("bool found = _bakedGraph?.TryPath", source);
        Assert.DoesNotContain("NavigationServer3D", source);
    }

    [Fact]
    public void CollisionReconciliationReentersAPhysicsFrameBeforeEachDirectSpaceQueryBatch()
    {
        if (FindRepositoryFile(Path.Combine("src", "Net", "ZombieNavigation.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        int loop = source.IndexOf("foreach (NavFlag flag in _flags)", StringComparison.Ordinal);
        int physicsFrame = source.IndexOf("SceneTree.SignalName.PhysicsFrame", loop,
            StringComparison.Ordinal);
        int ray = source.IndexOf("space.IntersectRay(ray)", loop, StringComparison.Ordinal);

        Assert.True(loop >= 0 && physicsFrame > loop && ray > physicsFrame,
            "every per-flag ray batch must enter a physics notification after worker/file awaits");
    }

    [Fact]
    public void DedicatedZombiesUseTheSameAuthoritativePhysicsWorldAsHostedZombies()
    {
        if (FindRepositoryFile(Path.Combine("src", "Net", "DedicatedServer.cs")) is not { } serverPath
            || FindRepositoryFile(Path.Combine("src", "Net", "NetworkManager.cs")) is not { } hostPath
            || FindRepositoryFile(Path.Combine("src", "Net", "ZombiePhysics.cs")) is not { } physicsPath)
            return;

        string server = File.ReadAllText(serverPath);
        string host = File.ReadAllText(hostPath);
        string physics = File.ReadAllText(physicsPath);
        Assert.Contains("BuildTerrainCollision(tiles, navigationField)", server);
        Assert.Contains("BuildObjectCollision(unturnedPath, level,", server);
        Assert.Contains("ZombiePhysics.Attach(zombies", server);
        Assert.Contains("ZombiePhysics.Attach(zombies", host);
        Assert.Contains("ZombieNavigation.Build(zombies.Navmesh, zombies.MoveResolver, level.Path)", server);
        Assert.Contains("PruneAgainstCollisionAsync(", server);
        Assert.Contains("PlayerConfig.StepOffset, selected, field", server);
        Assert.Contains("CollisionLayers.World | CollisionLayers.VisionBlocker", physics);
        Assert.Contains("physicalRay.To = to", physics);
        Assert.Contains("using Godot.Collections.Dictionary seen = space.IntersectRay(visionRay)", physics);
        Assert.Contains("using Godot.Collections.Dictionary blocked = space.IntersectRay(physicalRay)", physics);
        Assert.Contains("using Godot.Collections.Dictionary support = space.IntersectRay(stepDown)", physics);
        Assert.Contains("using Godot.Collections.Dictionary rest = space.GetRestInfo(query)", physics);
        Assert.Contains("using Godot.Collections.Dictionary hit = space.IntersectRay(snapRay)", physics);
        Assert.DoesNotContain("return space.IntersectRay", physics);
    }

    // Godot's CastMotion deliberately does not recover a shape that starts in contact. A zombie that
    // finished one tick tangent to a 20 cm fence could therefore get a clear cast for the next diagonal
    // tick and put its destination inside the fence. At 5.5 m/s the 80 ms motion is shorter than the
    // fence plus the capsule diameter, so destination overlap is both necessary and sufficient to stop
    // this thin-barrier tunnel. This ordering is a live-physics contract, like the body-attach rule above.
    [Fact]
    public void ZombieCapsuleChecksTheDestinationBeforeAcceptingAClearCast()
    {
        if (FindRepositoryFile(Path.Combine("src", "Net", "ZombiePhysics.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        int cast = source.IndexOf("float[] cast = space.CastMotion(query)", StringComparison.Ordinal);
        int destination = source.IndexOf("endpointRest = space.GetRestInfo(query)", cast,
            StringComparison.Ordinal);
        int accept = source.IndexOf("at += motion", cast, StringComparison.Ordinal);
        int unsafeContact = source.IndexOf("cast[1]", destination, StringComparison.Ordinal);
        int initialOverlap = source.IndexOf(
            "TryOverlapCorrection(space, query, motion, out Vector3 initialCorrection)",
            StringComparison.Ordinal);

        Assert.True(cast >= 0 && destination > cast && accept > destination,
            "a clear sweep may be accepted only after its destination capsule is proven unoccupied");
        Assert.True(initialOverlap >= 0 && initialOverlap < cast,
            "a long clear sweep may be accepted only after its starting overlap is recovered");
        Assert.True(unsafeContact > accept,
            "the slide normal must be queried at the first unsafe point, not the last safe point");
        Assert.Contains("float safeFraction = destinationOccupied ? 0f : cast[0]", source);
        Assert.Contains("stepDestinationOccupied", source);
    }

    [Fact]
    public void LegacyObjectIdsAreNormalizedBeforeEveryGuidKeyedWorldBuild()
    {
        if (FindRepositoryFile(Path.Combine("src", "World", "WorldBuilder.cs")) is not { } worldPath
            || FindRepositoryFile(Path.Combine("src", "Rendering", "ObjectStreamer.cs"))
                is not { } streamerPath)
            return;

        string world = File.ReadAllText(worldPath);
        string streamer = File.ReadAllText(streamerPath);
        Assert.Equal(2, CountOccurrences(world, "ResolvePlacementGuids(objects)"));
        Assert.Contains("_db.ResolvePlacementGuids(_objects)", streamer);
    }

    [Fact]
    public void HuntProbeAdvancesInsidePhysicsNotifications()
    {
        if (FindRepositoryFile(Path.Combine("src", "Net", "NetworkManager.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        int probe = source.IndexOf("if (OS.GetEnvironment(\"HUNT_PROBE\")", StringComparison.Ordinal);
        int frame = source.IndexOf("await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame)", probe,
            StringComparison.Ordinal);
        int tick = source.IndexOf("probe.Tick(views", probe, StringComparison.Ordinal);
        Assert.True(probe >= 0 && frame > probe && tick > frame,
            "the end-to-end probe must not issue DirectSpaceState queries from a timer callback");
    }

    [Fact]
    public void PathProbeQueriesInsideAPhysicsNotification()
    {
        if (FindRepositoryFile(Path.Combine("src", "Net", "NetworkManager.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        int probe = source.IndexOf("if (OS.GetEnvironment(\"PATH_PROBE\")", StringComparison.Ordinal);
        int frame = source.IndexOf("await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame)", probe,
            StringComparison.Ordinal);
        int query = source.IndexOf("zombies.PathQuery!(from, to", probe, StringComparison.Ordinal);
        Assert.True(probe >= 0 && frame > probe && query > frame,
            "the path probe must not validate stitched portals from a timer callback");
    }

    [Fact]
    public void BundleDecodePassesAreSerializedToBoundPeakMemory()
    {
        if (FindRepositoryFile(Path.Combine("src", "Rendering", "ObjectStreamer.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        int start = source.IndexOf("private void StartStreaming()", StringComparison.Ordinal);
        int end = source.IndexOf("private void DeferUnlessStopped", start, StringComparison.Ordinal);
        string method = source[start..end];

        Assert.DoesNotContain("Parallel.ForEach(pending", method);
        Assert.Contains("foreach (ContentExtraction.BundlePlan plan in pending)", method);
    }

    [Fact]
    public void PaletteDiscoveryUsesTheInaccessibleSubtreeSafeWalker()
    {
        if (FindRepositoryFile(Path.Combine("src", "Rendering", "MaterialResolver.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("SafeFileTree.EnumerateFiles(assetsDir, \"*.asset\")", source);
        Assert.Contains("catch (UnauthorizedAccessException)", source);
    }

    [Fact]
    public void MapMenuPassesTheExactSelectionKeyForWorkshopMaps()
    {
        if (FindRepositoryFile(Path.Combine("src", "UI", "MainMenu.cs")) is not { } menuPath
            || FindRepositoryFile(Path.Combine("src", "UI", "MapPicker.cs")) is not { } pickerPath)
            return;

        string menu = File.ReadAllText(menuPath);
        string picker = File.ReadAllText(pickerPath);

        // Play carries the exact selection key, because workshop folder names are not unique.
        Assert.Equal(1, CountOccurrences(menu, "OnStart?.Invoke(map.SelectionKey"));
        Assert.Contains("_maps[i].SelectionKey", picker);

        // Connect carries no map at all: the server names the level it runs and the client builds
        // that one. Passing the browser's selection here is what put a joining player on the wrong map.
        Assert.Equal(1, CountOccurrences(menu, "OnStart?.Invoke(\"\", address)"));
    }

    [Fact]
    public void RuntimeMultiMeshesUseOneRidOwnerWithExplicitLifecycle()
    {
        if (FindRepositoryFile(Path.Combine("src", "World", "MultiMeshRidRenderer.cs")) is not { } ownerPath
            || FindRepositoryFile(Path.Combine("src", "World", "FoliageBuilder.cs")) is not { } foliagePath
            || FindRepositoryFile(Path.Combine("src", "World", "ObjectsBuilder.cs")) is not { } objectsPath)
            return;

        string owner = File.ReadAllText(ownerPath);
        string foliage = File.ReadAllText(foliagePath);
        string objects = File.ReadAllText(objectsPath);
        Assert.Contains("RenderingServer.InstanceCreate()", owner);
        Assert.Contains("RenderingServer.InstanceSetScenario", owner);
        // The owner drives both ends of the range from the entry: an end alone fades a batch out with
        // distance, and a begin is what lets an authored lower LOD take over instead of drawing as well.
        Assert.Contains("InstanceGeometrySetVisibilityRange(instance, entry.VisibilityBegin,\n                    entry.VisibilityEnd,", owner);
        Assert.Contains("entry.VisibilityEnd > 0f || entry.VisibilityBegin > 0f", owner);
        Assert.Contains("GlobalTransform * entry.Transform", owner);
        Assert.Contains("RenderingServer.FreeRid(instance)", owner);
        Assert.True(owner.IndexOf("RenderingServer.FreeRid(instance)", System.StringComparison.Ordinal)
            < owner.LastIndexOf("_entries.Clear()", System.StringComparison.Ordinal));
        Assert.Contains("new MultiMeshRidRenderer { Name = \"Foliage\" }", foliage);
        Assert.Contains("rid.Add(multimesh", foliage);
        Assert.Contains("new MultiMeshRidRenderer { Name = label + \"Batches\" }", objects);
        // Objects reach the shared RID owner through AddLevels, which emits either one batch or the
        // LOD-0/LOD-1 pair; either way the batch itself still goes through AddRenderBatch.
        Assert.Contains("AddLevels(root, render, renderMesh, lodMesh", objects);
        Assert.Contains("AddRenderBatch(root, renderer, BuildMultiMesh", objects);
        // Batches carry their cell centre, not identity: Godot measures a visibility range from the
        // instance origin, so identity would switch every object on its distance from the map origin.
        Assert.Contains("renderer.Add(multimesh, new Transform3D(Basis.Identity, centre)", objects);
        Assert.Contains("BuildMultiMesh(mesh, transforms, bounds.Centre)", objects);
        Assert.Contains("SwitchDistanceFor(mesh, transforms) + bounds.Radius", objects);
        // The switch distance comes from the batch AddLevels was handed, so it accounts for the largest
        // scale actually placed there; deriving it from the mesh alone would swap scaled copies too early.
        Assert.Contains("SwitchDistanceFor(mesh, transforms)", objects);
        Assert.Contains("radius * maxScale * LodSwitchRadii", objects);
        if (FindRepositoryFile(Path.Combine("src", "Benchmark", "SceneMetrics.cs")) is { } metricsPath)
            Assert.Contains("case MultiMeshRidRenderer", File.ReadAllText(metricsPath));
    }

    [Fact]
    public void FoliageStreamingKeepsDecodeOffThreadAndRidLifecycleOnTheMainThread()
    {
        if (FindRepositoryFile(Path.Combine("src", "World", "FoliageStreamingRenderer.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("Task.Run(() => Decode", source);
        Assert.Contains("_index.DecodeChunk(index, cancellation.Token)", source);
        Assert.Contains("private void PublishDecoded", source);
        Assert.Contains("lock (_decodedGate)", source);
        Assert.Contains("_acceptDecoded = false", source);
        Assert.Contains("private void Upload(int index, FoliageChunk chunk)", source);
        Assert.Contains("RenderingServer.InstanceCreate()", source);
        Assert.Contains("visibilityEnd + FoliageBuilder.FadeMarginValue", source);
        Assert.Contains("private void Retire(int index)", source);
        Assert.Contains("RenderingServer.FreeRid(resident.Instance)", source);
        Assert.Contains("resident.Mesh.Dispose()", source);
        Assert.Contains("_lifetimeCancellation.Cancel()", source);
        Assert.Contains("_reservedDecodeBytes", source);
        Assert.DoesNotContain("Interlocked.Exchange(ref _reservedDecodeBytes, 0)", source);
        Assert.True(source.IndexOf("_lifetimeCancellation.Cancel()", StringComparison.Ordinal)
            < source.LastIndexOf("Retire(index)", StringComparison.Ordinal));

        // The emergency path is the streamer's only main-thread decode, so its cost must be timed, and
        // timed in a finally: a cancelled or failed decode still spent the frame time it spent, and
        // dropping those samples would make the total improve the more often the decode went wrong.
        Assert.Contains("long startedTicks = Stopwatch.GetTimestamp();", source);
        int started = source.IndexOf("long startedTicks", StringComparison.Ordinal);
        int finallyBlock = source.IndexOf("finally", started, StringComparison.Ordinal);
        int accumulate = source.IndexOf("_emergencyVisibleTicks += elapsed;", started, StringComparison.Ordinal);
        Assert.True(started >= 0 && finallyBlock > started && accumulate > finallyBlock);

        if (FindRepositoryFile(Path.Combine("src", "Benchmark", "SceneMetrics.cs")) is { } metricsPath)
            Assert.Contains("foliageOwner.StructuralChunks", File.ReadAllText(metricsPath));
        if (FindRepositoryFile(Path.Combine("src", "Benchmark", "GpuBenchmark.cs")) is { } gpuPath)
        {
            string gpu = File.ReadAllText(gpuPath);
            Assert.Contains("FoliageBenchmarkSettling.WaitAsync", gpu);
            Assert.Contains("metrics[\"foliage.settled\"] = settled ? 1 : 0;", gpu);
        }
    }

    // The spawn burst is the emergency path's largest single contribution: the first plan runs on the
    // frame the player appears, so every chunk already inside its visibility radius is decoded and
    // uploaded synchronously right then. The warm pass moves that work behind the loading screen, and is
    // only worth having while it keeps the properties that make it cheaper than the burst it replaces:
    // decodes off the main thread, uploads paced against a frame budget, and the steady loop held off
    // until it is done.
    [Fact]
    public void FoliagePrewarmDecodesOffThreadAndPacesItsUploadsBehindTheLoadingScreen()
    {
        if (FindRepositoryFile(Path.Combine("src", "World", "FoliageStreamingRenderer.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("public async Task PrewarmAsync(CancellationToken loadCancellation = default)", source);
        // The caller's load token, linked with the lifetime one. This node cannot leave the tree — and so
        // cannot cancel its own token — until the load teardown that waits on this pass has finished, so
        // without the caller's token a cancelled load sits through every remaining decode and upload.
        Assert.Contains("CreateLinkedTokenSource(_lifetimeCancellation.Token, loadCancellation)", source);
        // Batched decode on a worker, never DecodeChunk on the main thread the way the emergency path has to.
        Assert.Contains("_index.DecodeChunks(indices, token)", source);
        Assert.Contains("Task<WarmBatch> batch = Task.Run(", source);
        // Half the cap per batch: the batch being uploaded still holds its transforms while the next one
        // decodes, so it is the pair that has to fit the bound the pass claims to respect. A chunk larger
        // than half the cap gets a batch of its own, and its successor is serialized behind it rather
        // than pipelined over the bound — hence the exact pair check, not just the halved budget.
        string normalized = source.Replace("\r\n", "\n");
        Assert.Contains("FoliageResidencyPlanner.WarmBatches(plan, ExpectedDecodedBytes,\n"
            + "                Math.Max(1, _decodedByteLimit / 2));", normalized);
        Assert.Contains("held + BatchBytes(batches[batch + 1]) <= _decodedByteLimit", source);
        Assert.Contains("decoded = null;\n                if (more && !overlapped)", normalized);
        // What the pass holds is decoded-and-waiting like any other in-flight decode, so it belongs in
        // the counter maxDecodedBytes reports — otherwise a warm pass that answered the whole ring lets
        // the benchmark report a peak of zero while it was holding two batches — and anything it never
        // uploaded has to be given back, since the steady loop reads that counter before decoding.
        Assert.Contains("UpdateMaximum(ref _maxDecodedBytes, Interlocked.Add(ref _decodedBytes, bytes))",
            source);
        // Charged when the decode starts, not when it lands: an overlapped batch is already holding its
        // transforms while the previous one uploads, and accounting on arrival — after the previous
        // batch has been released chunk by chunk — would record one batch where two were held.
        Assert.Contains("private (Task<WarmBatch> Decoding, long Bytes) StartWarmDecode(", source);
        Assert.DoesNotContain("decoding = overlapped ? DecodeBatch(", source);
        Assert.Contains("Interlocked.Add(ref _decodedBytes, -uploaded);", source);
        Assert.Contains("Interlocked.Add(ref _decodedBytes, -carried);", source);
        Assert.Contains("if (outstandingBytes != 0)\n                Interlocked.Add(ref _decodedBytes, "
            + "-outstandingBytes);", normalized);
        // Worker wall time is not main-thread upload time: a deadline that expired while a decode was
        // still running would spend a whole frame on a single upload, once per batch.
        Assert.Contains("bool decodeWasPending = !decoding!.IsCompleted;", source);
        Assert.Contains("if (decodeWasPending)\n                    until = Stopwatch.GetTimestamp() "
            + "+ budget;", normalized);
        // Uploads are RenderingServer work and stay here, yielding on the load's own frame budget.
        Assert.Contains("await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);", source);
        // The steady loop must not plan against a half-warm residency set, and the pass must claim the
        // focus it planned for — otherwise the first _Process replans and takes the burst anyway.
        int process = source.IndexOf("public override void _Process", StringComparison.Ordinal);
        Assert.True(process > 0 && source.IndexOf("if (_warming)", process, StringComparison.Ordinal)
            < source.IndexOf("DrainDecoded();", process, StringComparison.Ordinal));
        Assert.Contains("_focused = true;\n            _lastFocus = focus;", normalized);
        // A cancelled load frees the node between frames; uploading past that hands RIDs to a dying
        // scenario, so liveness and tree membership are re-checked before every upload.
        Assert.Contains("!token.IsCancellationRequested && GodotObject.IsInstanceValid(this) "
            + "&& IsInsideTree()", source);
        Assert.Contains("if (!WarmMayContinue(token))", source);
        // Anything the pass could not warm goes back to the steady loop, which otherwise would not
        // replan until the camera moved — and would meet those chunks on the emergency path instead.
        Assert.Contains("_needsRefill = plan.PrefetchTruncated || incomplete;", source);
        // Opt out for A/B: the burst has to remain measurable against the pass that removes it.
        Assert.Contains("\"UG_FOLIAGE_PREWARM\"", source);

        if (FindRepositoryFile(Path.Combine("src", "Rendering", "ObjectStreamer.cs")) is { } streamerPath)
        {
            string streamer = File.ReadAllText(streamerPath).Replace("\r\n", "\n");
            // Both build paths warm, and both await it: an unawaited pass would upload into the same
            // frames as the texture apply it was staged behind.
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(streamer,
                @"await PrewarmFoliageAsync\(\);").Count);
            Assert.Contains("await foliage.PrewarmAsync(_loadCancellation.Token);", streamer);
            // _sceneBuilt is what releases _Process to apply textures. Setting it before the warm pass
            // would let the two spend their separate per-frame budgets in the same frames, which is the
            // staging the pass yields to preserve.
            // The warm pass consumes cancellation and returns normally, so the cold build has to recheck
            // before _sceneBuilt releases _Process and the world is published to subscribers that
            // BackToMenu is still tearing down.
            Assert.Contains("await PrewarmFoliageAsync();\n        // The warm pass consumes the "
                + "cancellation", streamer);
            Assert.Contains("if (_loadCancellation.IsCancellationRequested)\n            return;\n"
                + "        _sceneBuilt = true;", streamer);
            // Both build paths guard, not just the cold one: the warm task is not even stored for
            // CancelAsync to wait on, so falling through publishes a world already queued for deletion.
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(streamer,
                @"await PrewarmFoliageAsync\(\);\n        // The warm pass consumes").Count);
            // Textures are applied before the pass yields its frames, on both paths: a material that
            // reaches the renderer bare and is textured later makes it compile the pipelines twice.
            Assert.True(streamer.IndexOf("_registry.ApplyAllAvailable();", StringComparison.Ordinal)
                < streamer.IndexOf("await PrewarmFoliageAsync();", StringComparison.Ordinal));
            Assert.True(streamer.IndexOf("_appliedTextures += _registry.ApplyAllAvailable();",
                    StringComparison.Ordinal)
                < streamer.LastIndexOf("await PrewarmFoliageAsync();", StringComparison.Ordinal));
        }
    }

    // The residency snapshot is the only direct evidence of what spatial residency keeps in memory. A
    // machine that never settles within the wait is exactly the one whose report needs it, so neither
    // tier may put those counts back behind the settled flag — and an unsettled snapshot must land on
    // its own keys, so a mid-fill state can never be diffed against a settled baseline's steady set.
    [Fact]
    public void BenchmarkReportsResidencyCountsEvenWhenStreamingNeverSettles()
    {
        foreach (string file in new[] { "RuntimeBenchmark.cs", "GpuBenchmark.cs" })
        {
            if (FindRepositoryFile(Path.Combine("src", "Benchmark", file)) is not { } path)
                continue;

            string source = File.ReadAllText(path);
            string prefix = file == "RuntimeBenchmark.cs" ? "runtime.foliage" : "foliage";
            Assert.Contains("string state = settled ? \"\" : \"Unsettled\";", source);
            foreach (string metric in new[] { "residentChunks", "residentInstances", "residentBufferBytes" })
                Assert.Contains($"metrics[$\"{prefix}.{metric}{{state}}\"]", source);
            Assert.DoesNotContain("if (settled)", source);
            Assert.DoesNotContain("if (includeResidencySnapshot && foliage.IsSettled)", source);
            // Two unsettled runs would otherwise diff their mid-fill snapshots against each other and
            // call the scheduler's doing a regression, so the keys must stay out of classification.
            Assert.Contains("[\"Unsettled\"] = double.PositiveInfinity,", source);
        }
    }

    // Settling is the signal that the streamer submitted the whole visible set. A baseline that settled
    // against a run that timed out is less of the map drawn, so both tiers must score it higher-is-better;
    // the GPU tier scoring it as an improvement is what made a starved run look like a win.
    [Fact]
    public void BothBenchmarkTiersTreatSettledFoliageAsHigherIsBetter()
    {
        foreach ((string file, string key) in new[]
            { ("RuntimeBenchmark.cs", "runtime.foliage.settled"), ("GpuBenchmark.cs", "foliage.settled") })
        {
            if (FindRepositoryFile(Path.Combine("src", "Benchmark", file)) is not { } path)
                continue;

            string source = File.ReadAllText(path);
            int higher = source.IndexOf("HigherIsBetter", StringComparison.Ordinal);
            Assert.True(higher >= 0, $"{file} must declare HigherIsBetter");
            Assert.True(source.IndexOf($"\"{key}\"", higher, StringComparison.Ordinal) > higher,
                $"{file} must score {key} as higher-is-better");
        }
    }

    [Fact]
    public void ColdSceneBuildFaultsCompletionInsteadOfLeavingLoadingPending()
    {
        if (FindRepositoryFile(Path.Combine("src", "Rendering", "ObjectStreamer.cs")) is not { } path)
            return;
        // The cold build stages part of its work across frames, so it is a Task rather than a void
        // callback and settles _completion through a faulted continuation — the same shape the warm path
        // uses. What must not change is that a throw reaches _completion instead of leaving the loading
        // flow awaiting forever.
        string source = File.ReadAllText(path);
        int start = source.IndexOf("if (_meshesExtracted && !_coldBuildStarted)", StringComparison.Ordinal);
        Assert.True(start >= 0, "the cold build must keep its single-entry latch");
        int end = source.IndexOf("private ", start + 1, StringComparison.Ordinal);
        string body = source.Substring(start, end - start);
        Assert.Contains("_coldBuildTask = OnMeshesExtractedAsync();", body);
        Assert.Contains("_completion.TrySetException(t.Exception!.InnerExceptions)", body);
        Assert.Contains("TaskContinuationOptions.OnlyOnFaulted", body);
        // The build spans frames now, so a cancel can land mid-flight: the task has to be reachable from
        // CancelAsync, and the build itself must not attach a scene to a node already on its way out.
        Assert.Contains("await ObserveStopped(_coldBuildTask);", source);
        Assert.Contains("if (_loadCancellation.IsCancellationRequested)\n            return;", source);
    }

    [Fact]
    public void DistantZombieRigMirrorStartsInTheEnabledEngineState()
    {
        if (FindRepositoryFile(Path.Combine("src", "Net", "ZombiesView.cs")) is not { } path)
            return;
        string source = File.ReadAllText(path);
        Assert.Contains("public bool AnimationActive = true", source);
        Assert.Contains("nearby != avatar.AnimationActive", source);
        Assert.Contains("rig.ProcessMode = nearby ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled", source);
    }

    [Fact]
    public void ViewportBackupUsesEditorGlobalConfigurationStorage()
    {
        if (FindRepositoryFile(Path.Combine("addons", "unturned", "ViewportTuning.cs")) is not { } path)
            return;
        string source = File.ReadAllText(path);
        Assert.Contains("GetEditorPaths().GetConfigDir()", source);
        Assert.DoesNotContain("private const string BackupPath = \"user://", source);
        Assert.Contains("Directory.CreateDirectory", source);
    }

    [Fact]
    public void FoliageSelectionIsWorkerSafeAndKeepsTheLegacyFallback()
    {
        if (FindRepositoryFile(Path.Combine("src", "World", "FoliageBuilder.cs")) is not { } builderPath
            || FindRepositoryFile(Path.Combine("src", "Rendering", "ObjectStreamer.cs")) is not { } streamerPath
            || FindRepositoryFile(Path.Combine("src", "World", "WorldBuilder.cs")) is not { } worldPath)
            return;
        string builder = File.ReadAllText(builderPath);
        string streamer = File.ReadAllText(streamerPath);
        string world = File.ReadAllText(worldPath);
        Assert.Contains("System.Environment.GetEnvironmentVariable(\"UG_FOLIAGE_RESIDENCY\")", builder);
        Assert.Contains("if (_foliageIndex == null)", streamer);
        Assert.Contains("_foliage = LevelFoliageChunks.Load", streamer);
        Assert.Contains("if (foliageIndex == null)", world);
        Assert.Contains("foliageData = LevelFoliageChunks.Load", world);
    }

    [Fact]
    public void PostLoadReclaimHasMeasuredOneAndTwoPassControls()
    {
        if (FindRepositoryFile(Path.Combine("src", "LoadMemory.cs")) is not { } path)
            return;
        string source = File.ReadAllText(path);
        Assert.Contains("UG_RECLAIM_PASSES", source);
        Assert.Contains("Math.Clamp(configured, 0, 2)", source); // 0 is the "no compaction" A/B control
        Assert.Contains("if (passes == 2)", source);
        Assert.Contains("{passes} pass(es)", source);
    }

    // Every piece of one-time load work compacts when IT finishes. The loader's reclaim used to be the
    // only one, and it ran before the deferred audio extraction: on a cold cache that left a whole
    // bundle decode's transient committed for the rest of the session (~390 MB of RSS on PEI).
    [Fact]
    public void OneTimeWorkAfterTheLoadReclaimsItsOwnTransient()
    {
        if (FindRepositoryFile(Path.Combine("src", "Rendering", "ObjectStreamer.cs")) is not { } streamerPath
            || FindRepositoryFile(Path.Combine("src", "Main.cs")) is not { } mainPath)
        {
            return;
        }

        Assert.Contains("LoadMemory.Reclaim(\"post-load\")", File.ReadAllText(streamerPath));
        Assert.Contains("LoadMemory.Reclaim(\"audio extraction\")", File.ReadAllText(mainPath));
    }

    [Fact]
    public void ColliderAliasesShareParsedDataAndShapePoolKeys()
    {
        if (FindRepositoryFile(Path.Combine("src", "Rendering", "ColliderLibrary.cs")) is not { } libraryPath
            || FindRepositoryFile(Path.Combine("src", "World", "ObjectsBuilder.cs")) is not { } builderPath)
            return;
        string library = File.ReadAllText(libraryPath);
        string builder = File.ReadAllText(builderPath);
        Assert.Contains("ExactFileGroups.Build(sources, deduplicate)", library);
        Assert.Contains("foreach (Guid guid in group.Items) result[guid] = colliders", library);
        Assert.Contains("Dictionary<(List<CachedCollider>, int, long, long, long), int>", builder);
        Assert.Contains("Dictionary<(List<CachedCollider>, int), int>", builder);
        Assert.Contains("pool.Primitives.TryGetValue", builder);
        Assert.Contains("pool.Meshes.TryGetValue", builder);
    }

    [Fact]
    public void ObjectBodiesUseTheTestedFenceCollisionPolicy()
    {
        if (FindRepositoryFile(Path.Combine("src", "World", "ObjectsBuilder.cs")) is not { } path)
            return;
        string source = File.ReadAllText(path);
        Assert.Contains("ObjectCollisionPolicy.PhysicsLayer(asset!)", source);
    }

    [Fact]
    public void PhysicsRidOwnerPreservesBodyOrderingLifecycleAndDiagnosticNames()
    {
        if (FindRepositoryFile(Path.Combine("src", "World", "InstancedStaticBodies.cs")) is not { } ownerPath
            || FindRepositoryFile(Path.Combine("src", "World", "ObjectsBuilder.cs")) is not { } builderPath
            || FindRepositoryFile(Path.Combine("src", "Net", "NetworkManager.cs")) is not { } netPath)
            return;
        string owner = File.ReadAllText(ownerPath);
        string builder = File.ReadAllText(builderPath);
        string net = File.ReadAllText(netPath);
        int addShape = owner.IndexOf("PhysicsServer3D.BodyAddShape", System.StringComparison.Ordinal);
        int setSpace = owner.IndexOf("PhysicsServer3D.BodySetSpace", System.StringComparison.Ordinal);
        Assert.True(addShape >= 0 && setSpace > addShape);
        Assert.Contains("PhysicsServer3D.BodyAttachObjectInstanceId(body, GetInstanceId())", owner);
        Assert.Contains("_names[body] = definition.Name", owner);
        Assert.Contains("PhysicsServer3D.FreeRid(body)", owner);
        Assert.Contains("UG_NODE_PHYSICS", builder);
        Assert.Contains("InstancedStaticBodies.ColliderName", net);
    }

    [Fact]
    public void CollisionPlacementsCanBeWrittenDirectlyIntoFinalSpatialBuckets()
    {
        if (FindRepositoryFile(Path.Combine("src", "World", "ObjectsBuilder.cs")) is not { } path)
            return;
        string source = File.ReadAllText(path);
        Assert.Contains("UG_DIRECT_COLLISION_BUCKETS", source);
        Assert.Contains("buckets.Add(origin.X, origin.Z, (shape, transform))", source);
        Assert.Contains("foreach (((int x, int z), List<(int Shape, Transform3D Transform)> inCell)", source);
        Assert.Contains("directFlat ?? buckets!.Flatten()", source);
    }

    [Fact]
    public void RoadsUseOneArrayUploadPerMaterialWithLegacyAbControl()
    {
        if (FindRepositoryFile(Path.Combine("src", "World", "RoadsBuilder.cs")) is not { } path)
            return;
        string source = File.ReadAllText(path);
        Assert.Contains("UG_ROAD_ARRAYS", source);
        Assert.Contains("new Vector3[vertexCount]", source);
        Assert.Contains("new int[indexCount]", source);
        Assert.Contains("AddSurfaceFromArrays", source);
        Assert.Contains("else\n            foreach (KeyValuePair<int, (SurfaceTool", source);
    }

    [Fact]
    public void MaterialDedupKeyIncludesTheCompleteTextureCacheFile()
    {
        if (FindRepositoryFile(Path.Combine("src", "Rendering", "TextureRegistry.cs")) is not { } registryPath
            || FindRepositoryFile(Path.Combine("src", "Rendering", "ModelLibrary.cs")) is not { } libraryPath)
            return;
        string registry = File.ReadAllText(registryPath);
        string library = File.ReadAllText(libraryPath);
        Assert.Contains("UG_DEDUP_MATERIAL_CONTENT", registry);
        Assert.Contains("TextureIdentity.Resolve(_textureCacheDir, textureKey)", registry);
        Assert.Contains("registry.MaterialIdentity(sm.TextureKey)", library);
        Assert.Contains("sm.Color, sm.Blend, sm.Metallic, sm.Smoothness, sm.Cull", library);
        Assert.Contains("registry.Register(sm.TextureKey, shared)", library);

        // The identity is the whole cache file, not the key that names it: format, dimensions, mips,
        // filter mode and pixels all participate, so nothing merges materials that sample differently.
        if (FindRepositoryFile(Path.Combine("core", "Unity", "TextureIdentity.cs")) is not { } identityPath)
            return;
        string identity = File.ReadAllText(identityPath);
        Assert.Contains("ExactContentKey.File(path)", identity);
        Assert.Contains("TextureCache.IsCurrent(path)", identity);
    }

    [Fact]
    public void ColdLoadRegroupsItsMaterialsOnceTheTexturesTheyLackHaveSettled()
    {
        if (FindRepositoryFile(Path.Combine("src", "Rendering", "ObjectStreamer.cs")) is not { } streamerPath
            || FindRepositoryFile(Path.Combine("src", "Rendering", "MaterialTable.cs")) is not { } tablePath)
            return;

        string streamer = File.ReadAllText(streamerPath);
        string table = File.ReadAllText(tablePath);

        // Only the cold path records where its materials landed: a warm load resolves every identity while
        // it realises, so it has nothing to re-group and should not pay to remember the surfaces.
        Assert.Contains("_materials.TrackSurfaces()", streamer);
        Assert.Contains("_materialsSettled = false", streamer);

        // Hashing the texture cache is worker work; only the surface reassignment is the main thread's.
        Assert.Contains("Task.Run(\n                () => TextureIdentity.ResolveAll(cacheDir, provisional, cancellation), cancellation)",
            streamer);
        Assert.Contains("_registry.ResolvedIdentities(resolved)", streamer);
        Assert.Contains("_materials.Rededuplicate(_registry)", streamer);
        Assert.Contains("await ObserveStopped(_rededupTask);", streamer);

        // The pass reads the registry's identity map, so the load state has to outlive it.
        int gate = streamer.IndexOf("!_materialsSettled", StringComparison.Ordinal);
        int release = streamer.IndexOf("_registry.ReleaseLoadingIndexes()", StringComparison.Ordinal);
        Assert.True(gate >= 0 && release > gate);

        Assert.Contains("MaterialAliasPlan.Canonical(keys, registry.MaterialIdentities)", table);
        Assert.Contains("surface.Mesh.SurfaceSetMaterial(surface.Index, target)", table);
        Assert.Contains("registry.RegisterAlias(surface.TextureKey, target)", table);
    }

    [Fact]
    public void NavigationReconciliationStateIsReleasedOnlyAfterPublishAndCacheWrite()
    {
        if (FindRepositoryFile(Path.Combine("src", "Net", "ZombieNavigation.cs")) is not { } path)
            return;
        string source = File.ReadAllText(path);
        int publish = source.LastIndexOf("await PublishAsync", System.StringComparison.Ordinal);
        int queue = source.LastIndexOf("QueueCheckpoint(cachePath", System.StringComparison.Ordinal);
        // The checkpoints are written on a chain rather than awaited where they are queued (each await
        // there cost a whole frame). Awaiting that chain is what still puts the last write before the
        // state it describes is dropped.
        int drained = source.LastIndexOf("await _checkpoints;", System.StringComparison.Ordinal);
        int release = source.LastIndexOf("ReleaseReconciliationState();", System.StringComparison.Ordinal);
        Assert.True(publish >= 0 && queue > publish && drained > queue && release > drained);
        Assert.Contains("UG_KEEP_NAV_RECONCILE_STATE", source);
        Assert.Contains("_unreachable.Clear()", source);
    }

    [Fact]
    public void NavigationReconciliationResumesAtomicPerFlagCheckpoints()
    {
        if (FindRepositoryFile(Path.Combine("src", "Net", "ZombieNavigation.cs")) is not { } path)
            return;
        string source = File.ReadAllText(path);
        Assert.Contains("UG_PARTIAL_NAV_CACHE", source);
        Assert.Contains("TryReadPartial", source);
        Assert.Contains("if (!_unreachable.ContainsKey(flag))", source);
        Assert.Contains("QueueCheckpoint(cachePath, fingerprint, triangleCounts)", source);
        Assert.Contains("File.Move(temporary, cachePath, overwrite: true)", source);
    }

    [Fact]
    public void MapPreviewTexturesBelongToThePickerLifetimeByDefault()
    {
        if (FindRepositoryFile(Path.Combine("src", "UI", "MapPicker.cs")) is not { } path)
            return;
        string source = File.ReadAllText(path);
        Assert.Contains("readonly Dictionary<string, ImageTexture?> _textureCache", source);
        Assert.Contains("UG_STATIC_MAP_PREVIEW_CACHE", source);
        Assert.Contains("public override void _ExitTree()", source);
        Assert.Contains("_textureCache.Clear()", source);
    }

    [Fact]
    public void TerrainOccluderDataIsPreparedOffTheMainThreadAndEnabledByDefault()
    {
        if (FindRepositoryFile(Path.Combine("src", "World", "WorldBuilder.cs")) is not { } worldPath
            || FindRepositoryFile(Path.Combine("src", "World", "TerrainOccluder.cs")) is not { } occluderPath)
            return;
        string world = File.ReadAllText(worldPath);
        string occluder = File.ReadAllText(occluderPath);
        // Asserts the flag defaults ON. The spelling moved to EnvFlag so that "false" turns it off
        // instead of on — `!= "0"` compared the string without reading it.
        Assert.Contains("EnvFlag.IsOn(OS.GetEnvironment(\"TERRAIN_OCCLUDERS\"), whenUnset: true)", world);
        Assert.Contains("occluders[i] = TerrainOccluder.Prepare(meshes[i])", world);
        Assert.Contains("TerrainOccluder.Finish(occluders[i])", world);
        Assert.Contains("public static Prepared Prepare", occluder);
        Assert.Contains("public static OccluderInstance3D Finish", occluder);
    }

    [Fact]
    public void SparseObjectGroupsCrossingCellsArePartitionedWithAnAbControl()
    {
        if (FindRepositoryFile(Path.Combine("src", "World", "ObjectsBuilder.cs")) is not { } path)
            return;
        string source = File.ReadAllText(path);
        Assert.Contains("UG_CHUNK_SPARSE_OBJECTS", source);
        Assert.Contains("transforms.Count < MinChunkedInstances && spread", source);
        Assert.Contains("placementTriangles >= SparseChunkMinTriangles", source);
        Assert.Contains("UG_SPARSE_OBJECT_MIN_TRIS", source);
        Assert.Contains("transforms.Count >= MinChunkedInstances || sparseWide", source);
        Assert.Contains("sparseExtraBatches += cells.Count - 1", source);
        Assert.Contains("BuildFallbackBatch(inCell, mesh, material", source);
        Assert.Contains("cells.TryGetValue(cell", source);
    }

    [Fact]
    public void TheMeshLodThresholdIsAppliedWhetherOrNotTheOverrideIsSet()
    {
        if (FindRepositoryFile(Path.Combine("src", "Main.cs")) is not { } path)
            return;
        string source = File.ReadAllText(path);
        // The override used to be the only writer, so the shipped value was whatever the engine
        // defaulted to and dropping the assignment out of the unset path would silently restore it.
        // Ordering alone does not prove it: moving the write back inside the override block keeps the
        // default, the lookup and the write in the same order. What distinguishes the two shapes is
        // which side of the block's closing brace the write lands on, so find that brace and check.
        // The value itself is deliberately not pinned — it is tuning, and a test that fails when it is
        // retuned only teaches people to edit the test.
        source = source.Replace("\r\n", "\n");
        int fallback = source.IndexOf("float thresholdPixels = DefaultMeshLodThreshold", StringComparison.Ordinal);
        int overridden = source.IndexOf("thresholdPixels = requestedThreshold", StringComparison.Ordinal);
        Assert.True(fallback >= 0 && overridden > fallback,
            "the default has to be in hand before the override can replace it");
        // The block closes at method-body indentation, which the format check keeps stable.
        int blockEnd = source.IndexOf("\n        }", overridden, StringComparison.Ordinal);
        int applied = source.IndexOf("viewport.MeshLodThreshold = Mathf.Clamp(thresholdPixels, 0f, 1024f)",
            StringComparison.Ordinal);
        Assert.True(blockEnd >= 0 && applied > blockEnd,
            "the viewport write has to sit after the override block, or an unset override leaves the "
                + "engine default in place");
    }

    [Fact]
    public void ObjectCellsAreAnchoredPerGroupAndCoarsenedByTheGeometryTheyCarry()
    {
        if (FindRepositoryFile(Path.Combine("src", "World", "ObjectsBuilder.cs")) is not { } path)
            return;
        string source = File.ReadAllText(path);
        // Anchoring the grid at the world origin cuts every group straddling it along both axes whatever
        // the cell size is, so such a group can never fall below four batches — and official maps are
        // centred there. The anchor has to come from the group.
        Assert.Contains("AnchorOf(transforms)", source);
        Assert.Contains("(origin.X - anchor.X) / cellSize", source);
        Assert.Contains("(origin.Z - anchor.Z) / cellSize", source);
        // Splitting buys frustum rejection and costs a draw call per cell, so it pays in proportion to
        // the geometry each cell carries. Coarsening by doubling keeps the grids nested, which is what
        // makes the cell count fall monotonically and the walk terminate.
        Assert.Contains("UG_OBJECT_CELL_MIN_TRIS", source);
        Assert.Contains("trianglesPerInstance * transforms.Count / cells >= MinCellTriangles", source);
        Assert.Contains("metres *= 2f", source);
    }

    [Fact]
    public void RidOwnersDiscardUploadMetadataButRetainServerResourcesAndTransforms()
    {
        if (FindRepositoryFile(Path.Combine("src", "World", "InstancedStaticBodies.cs")) is not { } bodyPath
            || FindRepositoryFile(Path.Combine("src", "World", "MultiMeshRidRenderer.cs")) is not { } renderPath)
            return;
        string bodies = File.ReadAllText(bodyPath);
        string render = File.ReadAllText(renderPath);
        Assert.Contains("UG_KEEP_RID_UPLOAD_METADATA", bodies);
        Assert.Contains("_retainedShapePools.Add(definition.Shapes)", bodies);
        Assert.Contains("_definitions = new List<Definition>()", bodies);
        Assert.Contains("UG_KEEP_RID_UPLOAD_METADATA", render);
        Assert.Contains("_retainedMeshes.Add(entry.Mesh)", render);
        Assert.Contains("_localTransforms.Add(entry.Transform)", render);
        Assert.Contains("_entries = new List<Entry>()", render);
        Assert.Contains("NotificationVisibilityChanged", render);
        Assert.Contains("InstanceSetVisible(instance, visible)", render);
    }

    [Fact]
    public void FoliageUpload_PreparesOnlyABoundedChunkBatch()
    {
        if (FindRepositoryFile(Path.Combine("src", "World", "FoliageBuilder.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("PackBatchChunks", source);
        Assert.Contains("new PackedChunk[countInBatch]", source);
        Assert.DoesNotContain("new PackedChunk[groups.Count]", source);
    }

    [Fact]
    public void LoadingState_IsReleasedOnlyAfterSceneAndStreamingConsumersFinish()
    {
        if (FindRepositoryFile(Path.Combine("src", "Rendering", "ObjectStreamer.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("!_finished || !_materialsSettled || (_streamStarted && !_texturesDone)", source);
        Assert.Contains("_registry.ReleaseLoadingIndexes()", source);
        Assert.Contains("_plans.Clear()", source);
        Assert.Contains("_layersProduced.Clear()", source);
        Assert.Contains("DeferUnlessStopped(TryFinalizeLoadState)", source);
        Assert.Contains("if (_loadCancellation.IsCancellationRequested)", source);
        Assert.Contains("if (!_loadCancellation.IsCancellationRequested)", source);
    }

    [Fact]
    public void FailedLoadsCancelAndDrainTheirWorkersBeforeTheMenuReturns()
    {
        if (FindRepositoryFile(Path.Combine("src", "Main.cs")) is not { } mainPath
            || FindRepositoryFile(Path.Combine("src", "Rendering", "ObjectStreamer.cs")) is not { } streamerPath
            || FindRepositoryFile(Path.Combine("src", "Rendering", "ModelExtractor.cs")) is not { } extractorPath)
            return;

        string main = File.ReadAllText(mainPath);
        string streamer = File.ReadAllText(streamerPath);
        string extractor = File.ReadAllText(extractorPath);
        int cancel = main.IndexOf("await failedStreamer.CancelAsync();", StringComparison.Ordinal);
        int menu = main.IndexOf("var menu = new MainMenu", cancel, StringComparison.Ordinal);
        Assert.True(cancel >= 0 && menu > cancel);
        Assert.Contains("await ObserveStopped(_prepTask);", streamer);
        Assert.Contains("await ObserveStopped(_streamTask);", streamer);
        Assert.Contains("cancellationToken: cancellation", streamer);
        Assert.Contains("cancellationToken.IsCancellationRequested", extractor);
    }

    [Fact]
    public void EditorPreviewTreatsObjectsAndFoliageAsIndependentSelections()
    {
        if (FindRepositoryFile(Path.Combine("addons", "unturned", "WorldPreview.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("public bool NeedsPlacements => Objects || Foliage;", source);
        Assert.Contains("options.NeedsPlacements", source);
        Assert.Contains("if (options.Objects)", source);
        Assert.Contains("if (options.Foliage)", source);
    }

    [Fact]
    public void EditorWarmCachePlansAndWritesMissingTerrainLayers()
    {
        if (FindRepositoryFile(Path.Combine("addons", "unturned", "WorldPreview.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("MissingTerrainLayers(mapPath, sources)", source);
        Assert.Contains("TerrainLayerCache.Missing(needed, owners, TerrainCacheDir)", source);
        Assert.Contains("BundleTextures.ExtractStreamed(plan.Source.BundlePath", source);
        Assert.Contains("TerrainLayerCache.Write(material, texture, plan.Source.BundlePath, TerrainCacheDir)",
            source);
    }

    [Fact]
    public void AudioExtractionPreservesEverySerializedFileAndSelectsItsResource()
    {
        if (FindRepositoryFile(Path.Combine("src", "Rendering", "AudioExtractor.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("List<byte[]> SerializedFiles", source);
        Assert.Contains("Dictionary<string, byte[]> Resources", source);
        Assert.Contains("serialized.Add(stream.Read((int)node.Size))", source);
        Assert.Contains("foreach (byte[] bytes in nodes.SerializedFiles)", source);
        Assert.Contains("ResourceFor(clip.StreamFile, nodes.Resources)", source);
        Assert.DoesNotContain("sf = stream.Read((int)node.Size)", source);
        Assert.DoesNotContain("resource = stream.Read((int)node.Size)", source);
        // Off disk, never through a byte[] of the whole file: the fallback holds what it decompresses
        // and used to hold the compressed bundle on top of it.
        Assert.Contains("MasterBundleStream.OpenFile(bundlePath)", source);
    }

    // A cold load decodes the masterbundle once. The audio clips are byte ranges in the .resource node at
    // the end of the same blob the texture pass already walks to, so they are planned into that pass:
    // extracting them separately cost a second whole-bundle LZMA decode for 20 MB of clips, and it ran
    // after the load had compacted its heap, so its transient stayed resident for the whole session.
    [Fact]
    public void AudioClipsRideTheSameBundlePassAsTheTextures()
    {
        if (FindRepositoryFile(Path.Combine("src", "Rendering", "ModelExtractor.cs")) is not { } extractorPath
            || FindRepositoryFile(Path.Combine("src", "Rendering", "ObjectStreamer.cs")) is not { } streamerPath)
        {
            return;
        }

        // Normalized: the literals below span lines, and a checkout that materializes CRLF would make
        // every one of them miss on source that is perfectly correct.
        string extractor = File.ReadAllText(extractorPath).Replace("\r\n", "\n");
        // One plan across the bundle's serialized files, and only the clips a file added are owed: a raw
        // clip group is resolved a container at a time, so a per-file plan completed it from the first
        // file that held any of its clips and left the rest of them unextracted.
        Assert.Contains("AudioExtractor.Plan(file, audio, audioPlan)", extractor);
        Assert.Contains("audioOwed = OweAudioClips(audioPlan, audioOwed, i, ordered, owedByFile, written)",
            extractor);
        // A range in a node the decoder has already passed can never be served, and marking its
        // definition complete would cache it permanently without a clip the bundle does hold.
        Assert.Contains("plan.MarkUnservable(clip.Definition)", extractor);
        Assert.Contains("AudioExtractor.WriteClip(owed.AudioPlan, owed.AudioClip, bytes)", extractor);

        // def.bin is the cache's only completeness marker, so it is written on the pass's normal exits
        // only: a cancelled pass must leave the definition looking absent rather than half-extracted.
        int earlyExit = extractor.IndexOf("CompleteAudio(audioPlan);\n                return;",
            StringComparison.Ordinal);
        Assert.True(earlyExit > 0);
        int cancelled = extractor.IndexOf("if (cancellationToken.IsCancellationRequested)\n                return;",
            StringComparison.Ordinal);
        Assert.True(cancelled > 0);
        Assert.DoesNotContain("CompleteAudio", extractor.Substring(cancelled,
            extractor.IndexOf("owedByFile.Remove(fileName);", cancelled, StringComparison.Ordinal) - cancelled));

        // Planned before the pass starts: a forward-only decoder cannot go back for a range named after
        // it has gone past, and the .resource node is the last one in the blob.
        string streamer = File.ReadAllText(streamerPath);
        int planned = streamer.IndexOf("_audioWants = PlanAudio(unturnedPath);", StringComparison.Ordinal);
        int started = streamer.IndexOf("StartStreaming();", StringComparison.Ordinal);
        Assert.True(planned > 0 && started > planned);
        Assert.Contains("audio: audio,", streamer);
    }

    // Whatever may open a bundle of its own waits for the pass that is already reading one. Finished says
    // the scene is built, which is not the same thing: a warm mesh cache with cold terrain layers streams
    // a bundle while the scene finishes without it, and hanging the audio fallback off Finished let the
    // two decode the same file at once — the multi-gigabyte peak the passes are serialized to avoid, plus
    // two writers over one set of cache files.
    [Fact]
    public void TheAudioFallbackWaitsForTheBundlePassRatherThanTheBuiltScene()
    {
        if (FindRepositoryFile(Path.Combine("src", "Rendering", "ObjectStreamer.cs")) is not { } streamerPath
            || FindRepositoryFile(Path.Combine("src", "Main.cs")) is not { } mainPath)
        {
            return;
        }

        string streamer = File.ReadAllText(streamerPath);
        string main = File.ReadAllText(mainPath);
        Assert.Contains("streamer.ExtractionFinished += RunPendingAudioExtraction;", main);
        Assert.DoesNotContain("streamer.Finished += RunPendingAudioExtraction;", main);

        // Emitted from the one place that knows both halves: the scene is finished and no decoder can
        // still be reading (TryFinalizeLoadState's own guard).
        int guard = streamer.IndexOf(
            "if (_loadStateReleased || !_finished || !_materialsSettled || (_streamStarted && !_texturesDone))",
            StringComparison.Ordinal);
        int emitted = streamer.IndexOf("EmitSignal(SignalName.ExtractionFinished);", guard,
            StringComparison.Ordinal);
        Assert.True(guard > 0 && emitted > guard);
    }

    [Fact]
    public void StreamingTextureMissesAreRetriedAfterTheirFilesArrive()
    {
        if (FindRepositoryFile(Path.Combine("src", "Rendering", "TextureRegistry.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        int guard = source.IndexOf("if (loaded.tex != null)", StringComparison.Ordinal);
        int cache = source.IndexOf("_loaded[textureKey] = loaded;", guard, StringComparison.Ordinal);
        int result = source.IndexOf("return loaded;", cache, StringComparison.Ordinal);
        Assert.True(guard >= 0 && cache > guard && result > cache);
    }

    // RecordMisses blacklists every needed GUID a pass did NOT produce, and deletes its cache. A pass cut
    // short has produced nothing, so committing its misses turns every GUID it never reached into a
    // permanent fallback box — extraction is suppressed from then on, until the bundle stamp changes.
    // Both extraction paths must therefore check before calling it, and neither check is expressible as a
    // unit test over the real bundle: this pins the guard where it sits.
    [Fact]
    public void NeitherExtractionPathRecordsMissesFromACancelledPass()
    {
        if (FindRepositoryFile(Path.Combine("src", "Rendering", "ModelExtractor.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        int at = 0;
        int guarded = 0;
        while ((at = source.IndexOf("RecordMisses(bundlePath", at, StringComparison.Ordinal)) >= 0)
        {
            // The guard is the nearest cancellation check above the call, within the same block.
            string before = source[Math.Max(0, at - 600)..at];
            if (before.Contains("cancellationToken.IsCancellationRequested", StringComparison.Ordinal))
                guarded++;
            at += "RecordMisses(bundlePath".Length;
        }

        Assert.Equal(2, guarded); // the file-based pass and the streaming pass
    }

    // Cache completeness is decided per mesh, so a format that gains a new per-prefab artifact cannot be
    // detected by a missing file — an absent lower level looks the same as a prefab that never had one.
    // The magic is the only thing that forces one more extraction pass, and it must never go backwards:
    // UGM8 predates source-aware texture tags, UGM9 predates the authored LOD levels, UGMA kept every
    // level a prefab shipped instead of only the ones materially cheaper than the base mesh, UGMB
    // predates both the .resS vertex buffers and the winding fix for mirrored parts — so its meshes are
    // missing a vehicle's wheels while looking complete by their own rules — and UGMC cached the hidden
    // Dead/Ragdoll states as if they were the object's own geometry.
    [Fact]
    public void MeshCacheMagicInvalidatesEveryOlderExtraction()
    {
        if (FindRepositoryFile(Path.Combine("core", "Unity", "MeshCache.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("private const uint Magic = 0x444D4755;", source); // "UGMD"
    }

    // The source check above pins the constant; this one proves the constant does its job. A cache
    // written by any earlier format must be rejected, because rejection is the only thing that forces
    // the extra extraction pass — a per-mesh completeness check cannot tell a missing lower level from
    // a prefab that never had one.
    [Fact]
    public void MeshCacheRejectsEveryEarlierFormatAndAcceptsWhatItWrites()
    {
        var written = new MemoryStream();
        MeshCache.Write(written, new[] { Vector3.Zero, Vector3.Right, Vector3.Up },
            System.Array.Empty<Vector3>(), new[] { Vector2.Zero, Vector2.Zero, Vector2.Zero },
            new List<CachedSubmesh> { new(new[] { 0, 1, 2 }, Colors.White, "", UnityMaterial.Blend.Opaque, 0f, 0f, EShaderCull.Back) });
        byte[] current = written.ToArray();
        Assert.True(MeshCache.IsCurrent(current));

        // UGMC..UGM8
        foreach (uint stale in new uint[] { 0x434D4755, 0x424D4755, 0x414D4755, 0x394D4755, 0x384D4755 })
        {
            byte[] older = (byte[])current.Clone();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(older, stale);
            Assert.False(MeshCache.IsCurrent(older), $"a cache written as {stale:X} must be rejected");
        }
    }

    [Fact]
    public void StepProbeStartsFromColliderReadinessAndUsesCooperativeShutdown()
    {
        if (FindRepositoryFile(Path.Combine("src", "Main.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("streamer.MeshesReady += elapsedMs => _ = RunStepProbe(stepProbe);", source);
        Assert.Contains("AppShutdown.RequestQuit(GetTree());", source);
        Assert.DoesNotContain("for (int i = 0; i < 120; i++)", source);
    }

    [Fact]
    public void ScreenshotExitUsesCooperativeShutdown()
    {
        if (FindRepositoryFile(Path.Combine("src", "Main.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        int capture = source.IndexOf("private async System.Threading.Tasks.Task CaptureAndQuit",
            StringComparison.Ordinal);
        int nextMethod = source.IndexOf("private void SetupEnvironment", capture, StringComparison.Ordinal);
        Assert.True(capture >= 0 && nextMethod > capture);

        string method = source[capture..nextMethod];
        Assert.Contains("AppShutdown.RequestQuit(GetTree());", method);
        Assert.DoesNotContain("GetTree().Quit();", method);
    }

    [Fact]
    public void AudioDefinitionCacheKeysIncludeTheirFullAssetPath()
    {
        if (FindRepositoryFile(Path.Combine("src", "Rendering", "AudioExtractor.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("TextureKey.Discriminate(prefix, assetPath)", source);
        Assert.DoesNotContain("bundleTag + \"_\" + DefNameOf(assetPath)", source);
        // Definitions and raw clip groups write their clips through ONE path, so the sanitized file name
        // and the containment check cannot drift apart between them — they did when each loop had its own.
        Assert.Equal(1, CountOccurrences(source,
            "SafeCachePath.UniqueFileName(clip.Name, \"clip\", clip.ClipId, \".ogg\")"));
        Assert.Equal(1, CountOccurrences(source, "SafeCachePath.TryResolveChild"));
    }

    [Fact]
    public void WholeBundleAudioExtractionRunsOnePassAtATime()
    {
        if (FindRepositoryFile(Path.Combine("src", "Main.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        int start = source.IndexOf("_pendingAudioExtraction = () =>", StringComparison.Ordinal);
        int end = source.IndexOf("Log.Print($\"[audio] footsteps ready", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string block = source.Substring(start, end - start);
        int task = block.IndexOf("AppShutdown.Track(System.Threading.Tasks.Task.Run", StringComparison.Ordinal);
        int loop = block.IndexOf("foreach (AudioExtractor.Request request in audioRequests)",
            StringComparison.Ordinal);
        Assert.True(task >= 0 && loop > task);
        Assert.Equal(1, CountOccurrences(block, "Task.Run"));
        Assert.Contains("AudioExtractor.Extract(request.BundlePath, request.BundleTag, request.DefPaths,",
            block);
        // The cold load already extracted these inside its bundle pass, so this fallback must ask before
        // it decodes: without the check it opened a 1.4 GB bundle to discover it had nothing to do.
        Assert.Contains("AudioExtractor.IsSatisfied(request)", block);
    }

    [Fact]
    public void RuntimeBenchmarkAppliesFamilyThresholdsToAllWallClockMetrics()
    {
        if (FindRepositoryFile(Path.Combine("src", "Benchmark", "RuntimeBenchmark.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("[\"runtime.frameMs\"] = 0.15", source);
        Assert.Contains("[\"runtime.processMonitorMs.\"] = 0.15", source);
        Assert.Contains("[\"runtime.physicsMonitorMs.\"] = 0.15", source);
        Assert.Contains("ThresholdSuffixOverrides", source);
        Assert.Contains("[\".totalMs\"] = 0.15", source);
        Assert.Contains("[\".meanMs\"] = 0.15", source);
        Assert.Contains("[\".maxMs\"] = 0.15", source);
        Assert.Contains("[\"runtime.managedBytes\"] = 0.15", source);
        Assert.DoesNotContain("[\"runtime.managedLiveBytes\"]", source[source.IndexOf("private static BaselineDiffOptions DiffOptions", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void ThirdPersonCollisionRayRunsOnlyFromPhysicsCameraUpdate()
    {
        if (FindRepositoryFile(Path.Combine("src", "Player", "PlayerController.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        int apply = source.IndexOf("private void ApplyPerspective()", StringComparison.Ordinal);
        int place = source.IndexOf("private void PlaceThirdPersonCamera()", apply, StringComparison.Ordinal);
        Assert.True(apply >= 0 && place > apply);
        Assert.DoesNotContain("PlaceThirdPersonCamera();", source.Substring(apply, place - apply));

        int update = source.IndexOf("private void UpdateCamera(float dt)", StringComparison.Ordinal);
        Assert.True(update >= 0 && update < apply);
        Assert.Contains("PlaceThirdPersonCamera();", source.Substring(update, apply - update));
    }

    [Fact]
    public void FinishedSubscribersSeeNeededGuidsBeforeTheyAreReleased()
    {
        if (FindRepositoryFile(Path.Combine("src", "Rendering", "ObjectStreamer.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        const string helper = "private void EmitFinishedAndReleaseNeededGuids()";
        int helperStart = source.IndexOf(helper, StringComparison.Ordinal);
        Assert.True(helperStart >= 0);

        int finished = source.IndexOf("EmitSignal(SignalName.Finished);", helperStart);
        int clear = source.IndexOf("_neededGuids.Clear();", helperStart);
        Assert.True(finished >= 0 && clear > finished);
        Assert.Equal(2, CountOccurrences(source, "EmitFinishedAndReleaseNeededGuids();"));

        int finalizerStart = source.IndexOf("private void TryFinalizeLoadState()", StringComparison.Ordinal);
        int finalizerEnd = source.IndexOf("private TerrainLayerPlan.BundleWants LayerWantsFor", finalizerStart,
            StringComparison.Ordinal);
        Assert.True(finalizerStart > helperStart && finalizerEnd > finalizerStart);
        Assert.DoesNotContain("_neededGuids.Clear();", source.Substring(finalizerStart, finalizerEnd - finalizerStart));
    }

    [Fact]
    public void TerrainLayerCacheRequiresTheOwningBundleStamp()
    {
        if (FindRepositoryFile(Path.Combine("src", "World", "TerrainLayerCache.cs")) is not { } cachePath
            || FindRepositoryFile(Path.Combine("src", "Rendering", "ObjectStreamer.cs")) is not { } streamerPath
            || FindRepositoryFile(Path.Combine("src", "World", "TerrainLayers.cs")) is not { } layersPath)
            return;

        string cache = File.ReadAllText(cachePath);
        Assert.Contains("MatchesSource", cache);
        Assert.Contains("LastWriteTimeUtc", cache);
        Assert.Contains("File.WriteAllText(StampPathFor(material, cacheDirectory), stamp)", cache);
        Assert.Contains("TerrainLayerCache.Missing(needed, bundlePaths)", File.ReadAllText(streamerPath));
        Assert.Contains("TerrainLayerCache.Read(guid, bundlePath)", File.ReadAllText(layersPath));
    }

    [Fact]
    public void MeshesAreGroupedBeforeParallelPreparation()
    {
        if (FindRepositoryFile(Path.Combine("src", "Rendering", "ModelLibrary.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        int group = source.IndexOf("ExactFileGroups.Build", System.StringComparison.Ordinal);
        int prepare = source.IndexOf("PrepareRange(groups", System.StringComparison.Ordinal);
        Assert.True(group >= 0 && prepare > group);
        Assert.DoesNotContain("ExactContentKey.Bytes(data)", source);
    }

    [Fact]
    public void EditorCacheScanPublishesOnlyTheLatestMapRequest()
    {
        if (FindRepositoryFile(Path.Combine("addons", "unturned", "MapPreviewDock.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("int generation = ++_cacheScanGeneration;", source);
        Assert.Contains("generation != _cacheScanGeneration || !ReferenceEquals(Selected, map)", source);
        Assert.Contains("private void ScanInstall()\n    {\n        // Invalidate", source);
    }

    [Fact]
    public void EditorPreviewAlwaysRestoresBusyStateWhenTheSceneCloses()
    {
        if (FindRepositoryFile(Path.Combine("addons", "unturned", "MapPreviewDock.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        int load = source.IndexOf("private async void OnLoadPreview()", StringComparison.Ordinal);
        int clear = source.IndexOf("private void OnClearPreview()", load, StringComparison.Ordinal);
        Assert.True(load >= 0 && clear > load);
        string method = source.Substring(load, clear - load);
        Assert.Contains("finally", method);
        Assert.Contains("if (Alive)", method);
        Assert.Contains("SetBusy(false);", method);
    }

    [Fact]
    public void RuntimeBenchmarkOmitsEmptyPhysicsFrameBuckets()
    {
        if (FindRepositoryFile(Path.Combine("src", "Benchmark", "RuntimeBenchmark.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("AddFrameBucket(report.Metrics, \"withPhysics\", withPhysicsFrameMs)", source);
        Assert.Contains("AddFrameBucket(report.Metrics, \"withoutPhysics\", withoutPhysicsFrameMs)", source);
        Assert.Contains("if (values.Count == 0)\n            return;", source);
    }

    [Fact]
    public void GpuBenchmarkUsesFamilyThresholdsForAllCurrentAndFutureTimingPoses()
    {
        if (FindRepositoryFile(Path.Combine("src", "Benchmark", "GpuBenchmark.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("[\"gpu.frameMs.median.\"] = 0.10", source);
        Assert.Contains("[\"cpu.processMonitorMs.median.\"] = 0.10", source);
        Assert.Contains("[\"gpu.frameMs.median\"] = 0.10", source);
        Assert.Contains("[\"cpu.processMonitorMs.median\"] = 0.10", source);
        Assert.DoesNotContain("cpu.processMonitorMs.median.ground", source);
    }

    [Fact]
    public void GpuBenchmarkSnapshotsSettledFoliageBeforeTheOptionalScreenshotPose()
    {
        if (FindRepositoryFile(Path.Combine("src", "Benchmark", "GpuBenchmark.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        int settle = source.IndexOf("await FoliageBenchmarkSettling.WaitAsync", StringComparison.Ordinal);
        int collect = source.IndexOf("SceneMetricsResult sm", settle, StringComparison.Ordinal);
        int shot = source.IndexOf("string shotPath", collect, StringComparison.Ordinal);
        Assert.True(settle >= 0 && collect > settle && shot > collect);
    }

    [Fact]
    public void RuntimeBenchmarkSettlesBeforeSnapshottingFoliageResidency()
    {
        if (FindRepositoryFile(Path.Combine("src", "Benchmark", "RuntimeBenchmark.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        int disable = source.IndexOf("RuntimeCounters.Disable();", StringComparison.Ordinal);
        int settle = source.IndexOf("await FoliageBenchmarkSettling.WaitAsync", disable,
            StringComparison.Ordinal);
        int report = source.IndexOf("var report = new BenchmarkReport", settle, StringComparison.Ordinal);
        int snapshot = source.IndexOf("AddFoliageMetrics(tree, report.Metrics, foliageSettled)", report,
            StringComparison.Ordinal);
        Assert.True(disable >= 0 && settle > disable && report > settle && snapshot > report);
        // The settling wait still runs before the snapshot, so a machine that drains its queue reports
        // the same stable counts it always did; only the omission on a machine that cannot settle is gone.
        Assert.Contains("bool settled = includeResidencySnapshot && foliage.IsSettled;", source);
    }

    [Fact]
    public void FetchGameDataRequiresEveryExpectedHeightmapAndRecordsAllMapCompletion()
    {
        if (FindRepositoryFile(Path.Combine("scripts", "fetch-game-data.sh")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("map_tile_bounds", source);
        Assert.Contains("PEI|Washington|Yukon", source);
        Assert.Contains("expected_count", source);
        Assert.Contains("write_completion_marker", source);
        Assert.Contains("verify_content \"$dest\" 0", source);
        Assert.Contains("all_maps \"$root\"", source);
        Assert.Contains("minimum_unknown_tiles=\"${3:-1}\"", source);
        Assert.DoesNotContain("expected_all_maps", source);
        Assert.DoesNotContain("any_map_is_whole", source);

        // A map directory with no Level.dat yet is what an interrupted --maps all leaves behind. Deriving
        // the expected set from Level.dat alone would drop it from both the set and the receipt, so the
        // maps that did finish would verify and publish a receipt that never names the one that did not.
        // DepotDownloader's staging skeleton is the record of what it actually materialized.
        int allMaps = source.IndexOf("all_maps() {", StringComparison.Ordinal);
        int allMapsEnd = source.IndexOf("\n}", allMaps, StringComparison.Ordinal);
        Assert.True(allMaps >= 0 && allMapsEnd > allMaps);
        string body = source[allMaps..allMapsEnd];
        Assert.Contains("staging=\"$1/.DepotDownloader/staging/Maps\"", body);
        Assert.Contains("dirs=(\"$staging\"/*/)", body);

        // The fallback, for a tree this script did not produce: the game keeps editor scratch under Maps/
        // (MapCatalogTests models it), so a directory that is not a map must not fail the run for lacking
        // a Level.dat it was never going to have.
        Assert.Contains("-s \"$dir/Level.dat\" || -d \"$dir/Landscape\" || -d \"$dir/Level\"", body);
    }

    [Fact]
    public void FetchGameDataManifestKeyUsesPortableDirectoryIteration()
    {
        if (FindRepositoryFile(Path.Combine("scripts", "fetch-game-data.sh")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("for manifest in \"$key_dir\"/manifest_*.txt", source);
        Assert.DoesNotContain("find \"$key_dir\" -maxdepth", source);
    }

    [Fact]
    public void CloudSetupQuarantinesAnIncompleteConfiguredInstall()
    {
        if (FindRepositoryFile(Path.Combine("scripts", "setup-cloud-env.sh")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("quarantine_incomplete_content", source);
        Assert.Contains("mktemp -d \"${content_dir}.incomplete.XXXXXX\"", source);
        Assert.Contains("mv -f -- \"$content_dir\" \"$quarantine/\"", source);
        Assert.Contains("quarantine_incomplete_content || quarantine_status=1", source);
        Assert.DoesNotContain("quarantine_incomplete_content || true", source);

        // UNTURNED_PATH=/opt/unturned/ would otherwise put the quarantine directory inside the tree it
        // replaces, and mv refuses to move a directory into its own child.
        Assert.Contains("content_dir=\"${content_dir%/}\"", source);

        // The two setup halves fail independently. Quarantining after the toolchain's exit would skip it
        // entirely when both fail, leaving the partial tree where UNTURNED_PATH resolves it next session.
        int quarantine = source.IndexOf("quarantine_incomplete_content || quarantine_status=1",
            StringComparison.Ordinal);
        int toolchainExit = source.IndexOf("exit \"$toolchain_status\"", StringComparison.Ordinal);
        Assert.True(quarantine >= 0 && toolchainExit > quarantine);
    }

    [Fact]
    public void RealDataCacheKeyIncludesTheContentReceiptSchema()
    {
        if (FindRepositoryFile(Path.Combine(".github", "workflows", "real-data.yml")) is not { } path)
            return;

        string source = File.ReadAllText(path);

        // Cache entries are immutable for a given key: trusting the hit flag would wedge every run behind
        // one bad entry until the depot manifest moves. Verify what came back, and re-fetch when it fails.
        Assert.DoesNotContain("steps.content-cache.outputs.cache-hit", source);

        // A repair has to outlive the run that made it. actions/cache skips its teardown save after an
        // exact-key hit, so a combined restore+save would fix the working directory and discard the fix,
        // leaving every later run to restore the same bad entry and download again.
        Assert.DoesNotContain("uses: actions/cache@v6\n        with:\n          path: build/game-data", source);

        // Asserted per job rather than by counting across the file. Totals are the weaker check: a job
        // that drops its save while another grows a duplicate step keeps every count intact, and the
        // point of this gate is that each job which restores the tree carries its own whole contract.
        // A consumer is identified by the path it caches, not by a step's display name. A rename, or a
        // new job that spells its step differently, would drop out of a name-based filter -- and since
        // the only count here is a lower bound, the job would then go unchecked while this still passed.
        IReadOnlyList<(string Name, string Body)> jobs = WorkflowJobs(source);
        var consumers = new List<(string Name, string Body)>();
        foreach ((string name, string body) in jobs)
            if (body.Contains("path: build/game-data", StringComparison.Ordinal))
                consumers.Add((name, body));

        Assert.True(consumers.Count >= 2,
            $"expected at least the test and structural jobs to restore content, found {consumers.Count}");

        foreach ((string name, string body) in consumers)
        {
            // Three references: the restore key, its restore-keys prefix, and the run-scoped save key.
            Assert.Equal(3, CountOccurrences(body,
                "unturned-content-receipt-v2-${{ steps.content-key.outputs.key }}"));
            Assert.True(
                CountOccurrences(body, "if ./scripts/fetch-game-data.sh --verify 2> /dev/null; then") == 1,
                $"job '{name}' does not verify what the cache handed back");
            Assert.True(CountOccurrences(body, "uses: actions/cache/restore@v6") == 1,
                $"job '{name}' does not restore the content cache exactly once");
            Assert.True(CountOccurrences(body, "uses: actions/cache/save@v6") == 1,
                $"job '{name}' does not save the content cache exactly once, so a repair would be discarded");
            Assert.True(CountOccurrences(body,
                    "key: unturned-content-receipt-v2-${{ steps.content-key.outputs.key }}-${{ github.run_id }}-${{ github.job }}") == 1,
                $"job '{name}' does not save under a run-scoped key");
        }
    }

    // Splits a workflow into its jobs by the two-space-indented `name:` lines under `jobs:`. Enough
    // structure for the assertions above without taking a YAML parser dependency into the test project.
    private static IReadOnlyList<(string Name, string Body)> WorkflowJobs(string source)
    {
        var jobs = new List<(string, string)>();
        string[] lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string? current = null;
        var body = new StringBuilder();
        bool inJobs = false;

        foreach (string line in lines)
        {
            if (line.StartsWith("jobs:", StringComparison.Ordinal))
            {
                inJobs = true;
                continue;
            }
            if (!inJobs)
                continue;
            // A column-zero comment is valid YAML and a normal way to separate sections, so it is not
            // the end of the jobs block. Mistaking one for a new top-level key would silently drop every
            // job below it -- and this gate would then pass for jobs it never looked at.
            if (line.StartsWith("#", StringComparison.Ordinal))
                continue;
            // A new top-level key does end it.
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
                break;

            if (JobHeader.IsMatch(line))
            {
                if (current != null)
                    jobs.Add((current, body.ToString()));
                current = line.Trim().TrimEnd(':');
                body.Clear();
                continue;
            }

            body.Append(line).Append('\n');
        }

        if (current != null)
            jobs.Add((current, body.ToString()));
        return jobs;
    }

    // A job header: two-space indented, a bare key, nothing after the colon.
    private static readonly Regex JobHeader = new(@"^  [A-Za-z0-9_-]+:\s*$", RegexOptions.Compiled);

    [Fact]
    public void StructuralMetricsScriptFindsTheReportOnEveryHostPlatform()
    {
        if (FindRepositoryFile(Path.Combine("scripts", "check-structural-metrics.sh")) is not { } path)
            return;

        // Godot's user:// lands somewhere different on each host. Looking only under Linux's tree would
        // report that a benchmark wrote nothing, on a macOS or Windows run that succeeded.
        string source = File.ReadAllText(path);
        Assert.Contains("$HOME/Library/Application Support/Godot/app_userdata/unturned-godot", source);
        Assert.Contains("${APPDATA:-$HOME/AppData/Roaming}", source);
        Assert.Contains("${XDG_DATA_HOME:-$HOME/.local/share}/godot/app_userdata/unturned-godot", source);
    }

    [Theory]
    [InlineData("GpuBenchmark.cs", "tree.Quit(failed ? 1 : 0);")]
    [InlineData("RuntimeBenchmark.cs", "AppShutdown.RequestQuit(tree, failed ? 1 : 0);")]
    public void WindowedBenchmarkTiersQuitNonzeroWhenNoReportWasWritten(string file, string quit)
    {
        if (FindRepositoryFile(Path.Combine("src", "Benchmark", file)) is not { } path)
            return;

        // These tiers catch their own exceptions so the tree still tears down. Quitting zero from that
        // path would tell scripts/run-benchmark.sh a measurement was taken when no report exists.
        string source = File.ReadAllText(path);
        int flag = source.IndexOf("bool failed = true;", StringComparison.Ordinal);
        int cleared = source.IndexOf("failed = false;", flag, StringComparison.Ordinal);
        int finish = source.IndexOf("BenchmarkRunner.Finish(", flag, StringComparison.Ordinal);
        Assert.True(flag >= 0 && finish > flag && cleared > finish);
        Assert.Contains(quit, source);
    }

    [Fact]
    public void RuntimeBenchmarkTreatsSettledFoliageAsHigherIsBetter()
    {
        if (FindRepositoryFile(Path.Combine("src", "Benchmark", "RuntimeBenchmark.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        int higher = source.IndexOf("HigherIsBetter", StringComparison.Ordinal);
        int settled = source.IndexOf("\"runtime.foliage.settled\"", higher, StringComparison.Ordinal);
        Assert.True(higher >= 0 && settled > higher);
    }

    [Fact]
    public void BenchmarkScriptAcceptsWorkshopSelectionKeys()
    {
        if (FindRepositoryFile(Path.Combine("scripts", "run-benchmark.sh")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("[[ \"$MAP\" == workshop:* ]]", source);
        Assert.Contains("workshop_map=\"${MAP#workshop:}\"", source);
        Assert.Contains("\"$workshop_map/Level.dat\"", source);
        Assert.Contains("--verify --maps \"$MAP\"", source);

        // MapCatalog.IsSupported is TileCount > 0. A legacy workshop map has Level.dat and no Landscape
        // tiles, so accepting it on Level.dat alone would benchmark an empty world and report it. The
        // count has to follow LevelInfo.EnumerateTiles' naming rule, not a bare *.heightmap glob: a
        // stray or malformed file makes the glob nonempty while TileCount stays zero.
        Assert.Contains("^Tile_(-?[0-9]+)_(-?[0-9]+)_Source\\.heightmap$", source);
        Assert.Contains("${#workshop_tiles[@]} -eq 0", source);
    }

    [Fact]
    public void BenchmarkScriptBuildsTheManagedProjectUnlessExplicitlySkipped()
    {
        if (FindRepositoryFile(Path.Combine("scripts", "run-benchmark.sh")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        int tierValidation = source.IndexOf("case \"$tier\" in", StringComparison.Ordinal);
        int skipGuard = source.IndexOf("${UG_BENCH_SKIP_BUILD:-0}", tierValidation,
            StringComparison.Ordinal);
        int build = source.IndexOf("dotnet build \"$repo_dir/unturned-godot.csproj\"", skipGuard,
            StringComparison.Ordinal);
        int launch = source.IndexOf("case \"$tier\" in", tierValidation + 1, StringComparison.Ordinal);

        Assert.True(tierValidation >= 0 && skipGuard > tierValidation && build > skipGuard,
            "the benchmark must not let Godot silently run a stale managed assembly");
        Assert.True(launch > build, "the managed build must finish before any benchmark tier launches");
        Assert.Contains("command -v dotnet", source);
    }

    [Fact]
    public void ShutdownKeepsTheFirstFailureCodeAcrossRepeatedQuitRequests()
    {
        if (FindRepositoryFile(Path.Combine("src", "AppShutdown.cs")) is not { } path)
            return;

        // A pause-menu quit or an expired QUIT_AFTER can ask to leave before Tier 3 reports its failure.
        // The IsShuttingDown early return makes every later call a no-op, so the code cannot be captured
        // after it -- a dropped 1 there is a failed benchmark that reports success.
        string source = File.ReadAllText(path);
        int capture = source.IndexOf("if (exitCode != 0 && ExitCode == 0)", StringComparison.Ordinal);
        int guard = source.IndexOf("if (IsShuttingDown)", capture, StringComparison.Ordinal);
        Assert.True(capture >= 0 && guard > capture);

        // Read when the deferred call runs, not when it is scheduled, so a failure raised in between lands.
        Assert.Contains("Callable.From(() => tree.Quit(ExitCode)).CallDeferred();", source);
        Assert.Contains("tree.Quit(ExitCode);", source);

        // Capturing a late failure is not enough by itself: a quit the tier never asked for lands on the
        // next idle frame, sooner than the tier gets a frame to reach its finally, so no failure is ever
        // raised to capture. Leaving mid-measurement has to fail on this side, before the capture.
        int inFlight = source.IndexOf("if (exitCode == 0 && BenchmarkInFlight)", StringComparison.Ordinal);
        Assert.True(inFlight >= 0 && inFlight < capture);
    }

    [Fact]
    public void RuntimeBenchmarkOwnsTheExitStatusUntilItsReportIsWritten()
    {
        if (FindRepositoryFile(Path.Combine("src", "Benchmark", "RuntimeBenchmark.cs")) is not { } path)
            return;

        // The bracket has to open before the sampling loop and close only once Finish has run, or an
        // early quit lands inside the window and still exits zero.
        string source = File.ReadAllText(path);
        int begin = source.IndexOf("AppShutdown.BeginBenchmark();", StringComparison.Ordinal);
        int finish = source.IndexOf("BenchmarkRunner.Finish(", begin, StringComparison.Ordinal);
        int end = source.IndexOf("AppShutdown.EndBenchmark();", finish, StringComparison.Ordinal);
        Assert.True(begin >= 0 && finish > begin && end > finish);
    }

    [Fact]
    public void NavigationBenchmarkClearsItsReusableRouteForEveryQuery()
    {
        if (FindRepositoryFile(Path.Combine("tools", "PerfHarness", "Program.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("NavReconcileCache.TryReadMetadata(input, out fingerprint, out triangles)", source);
        Assert.Contains("NavReconcileCache.TryReadPartial(input, fingerprint, triangles, out sets)", source);
        Assert.Contains("foreach ((Vector3 from, Vector3 to) in queries)\n            {\n                route.Clear();",
            source);
    }

    // Walks up from the test assembly to the repository root (the folder holding the solution).
    //
    // Null means one thing only: the repository root itself could not be found, which is a run from
    // outside the tree with genuinely nothing to check. A file missing UNDER a located root is the
    // opposite situation — the thing this test guards was renamed or deleted — and that is precisely
    // when it has to fail. Returning null there made every caller's `is not { } path) return;` into a
    // pass, so the tests failed open on exactly the refactor most likely to break the invariant.
    //
    // Measured before changing it: renaming src/World/InstancedStaticBody.cs left all 67 tests in this
    // file green, including the one that exists to read it.
    private static string? FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "unturned-godot.sln")))
            {
                string candidate = Path.Combine(directory.FullName, relativePath);
                Assert.True(File.Exists(candidate),
                    $"'{relativePath}' does not exist. This test reads that file and asserts on its "
                    + "contents, so it cannot pass without it: either update the path to where the code "
                    + "moved, or delete this test along with the thing it was guarding.");
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
