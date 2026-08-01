using System;
using System.IO;
using System.Text.RegularExpressions;
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
    public void LargeNavigationGraph_IsBuiltOffTheMainThreadAfterReconciliation()
    {
        if (FindRepositoryFile(Path.Combine("src", "Net", "ZombieNavigation.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("await Task.Run(() =>", source);
        Assert.Contains("BakedNavGraph.Build(_flags, _unreachable)", source);
        Assert.Contains("await PublishAsync(fingerprint, cachePath + \".csr\")", source);
        Assert.Contains("await PublishAsync(fingerprint, cachePath == null ? null : cachePath + \".csr\")", source);
        Assert.Contains("BakedNavGraph.TryRead", source);
        Assert.Contains("built.Write(output, fingerprint)", source);
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
        Assert.Equal(2, CountOccurrences(menu, "OnStart?.Invoke(map.SelectionKey"));
        Assert.Contains("_maps[i].SelectionKey", picker);
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
        Assert.Contains("InstanceGeometrySetVisibilityRange(instance, 0f, entry.VisibilityEnd,\n                    0f, entry.VisibilityMargin", owner);
        Assert.Contains("GlobalTransform * entry.Transform", owner);
        Assert.Contains("RenderingServer.FreeRid(instance)", owner);
        Assert.True(owner.IndexOf("RenderingServer.FreeRid(instance)", System.StringComparison.Ordinal)
            < owner.LastIndexOf("_entries.Clear()", System.StringComparison.Ordinal));
        Assert.Contains("new MultiMeshRidRenderer { Name = \"Foliage\" }", foliage);
        Assert.Contains("rid.Add(multimesh", foliage);
        Assert.Contains("new MultiMeshRidRenderer { Name = \"ObjectBatches\" }", objects);
        Assert.Contains("AddRenderBatch(root, render, BuildMultiMesh", objects);
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

        if (FindRepositoryFile(Path.Combine("src", "Benchmark", "SceneMetrics.cs")) is { } metricsPath)
            Assert.Contains("foliageOwner.StructuralChunks", File.ReadAllText(metricsPath));
        if (FindRepositoryFile(Path.Combine("src", "Benchmark", "GpuBenchmark.cs")) is { } gpuPath)
        {
            string gpu = File.ReadAllText(gpuPath);
            Assert.Contains("FoliageBenchmarkSettling.WaitAsync", gpu);
            Assert.Contains("if (settled)", gpu);
        }
    }

    [Fact]
    public void ColdSceneBuildFaultsCompletionInsteadOfLeavingLoadingPending()
    {
        if (FindRepositoryFile(Path.Combine("src", "Rendering", "ObjectStreamer.cs")) is not { } path)
            return;
        string source = File.ReadAllText(path);
        int method = source.IndexOf("private void OnMeshesExtracted()", StringComparison.Ordinal);
        int nextMethod = source.IndexOf("public override void _Process", method + 1, StringComparison.Ordinal);
        Assert.True(method >= 0 && nextMethod > method);
        string body = source.Substring(method, nextMethod - method);
        Assert.Contains("try", body);
        Assert.Contains("catch (Exception e)", body);
        Assert.Contains("_completion.TrySetException(e)", body);
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
        if (FindRepositoryFile(Path.Combine("src", "Rendering", "ObjectStreamer.cs")) is not { } path)
            return;
        string source = File.ReadAllText(path);
        Assert.Contains("UG_RECLAIM_PASSES", source);
        Assert.Contains("Math.Clamp(configured, 1, 2)", source);
        Assert.Contains("if (passes == 2)", source);
        Assert.Contains("{passes} pass(es)", source);
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
        Assert.Contains("ExactContentKey.File(path)", registry);
        Assert.Contains("registry.MaterialIdentity(sm.TextureKey)", library);
        Assert.Contains("sm.Color, sm.Blend, sm.Metallic, sm.Smoothness, sm.Cull", library);
        Assert.Contains("registry.Register(sm.TextureKey, shared)", library);
    }

    [Fact]
    public void NavigationReconciliationStateIsReleasedOnlyAfterPublishAndCacheWrite()
    {
        if (FindRepositoryFile(Path.Combine("src", "Net", "ZombieNavigation.cs")) is not { } path)
            return;
        string source = File.ReadAllText(path);
        int publish = source.LastIndexOf("await PublishAsync", System.StringComparison.Ordinal);
        int write = source.LastIndexOf("await PersistCheckpointAsync", System.StringComparison.Ordinal);
        int release = source.LastIndexOf("ReleaseReconciliationState();", System.StringComparison.Ordinal);
        Assert.True(publish >= 0 && write > publish && release > write);
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
        Assert.Contains("if (_unreachable.ContainsKey(flag))", source);
        Assert.Contains("await PersistCheckpointAsync", source);
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
        Assert.Contains("OS.GetEnvironment(\"TERRAIN_OCCLUDERS\") != \"0\"", world);
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
        Assert.Contains("!_finished || (_streamStarted && !_texturesDone)", source);
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
        Assert.Contains("ResourceFor(res, nodes.Resources)", source);
        Assert.DoesNotContain("sf = stream.Read((int)node.Size)", source);
        Assert.DoesNotContain("resource = stream.Read((int)node.Size)", source);
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

    [Fact]
    public void SourceAwareTextureTagsInvalidateNameOnlyMeshCaches()
    {
        if (FindRepositoryFile(Path.Combine("core", "Unity", "MeshCache.cs")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("\"UGM9\"", source);
        Assert.Contains("private const uint Magic = 0x394D4755;", source);
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
        Assert.Equal(2, CountOccurrences(source,
            "SafeCachePath.UniqueFileName(name, \"clip\", clipId, \".ogg\")"));
        Assert.Equal(2, CountOccurrences(source, "SafeCachePath.TryResolveChild"));
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
        int loop = block.IndexOf("foreach ((string bundle", StringComparison.Ordinal);
        Assert.True(task >= 0 && loop > task);
        Assert.Equal(1, CountOccurrences(block, "Task.Run"));
        Assert.Contains("AudioExtractor.Extract(bundle, tag, paths, audioCacheDir, groups);", block);
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
        Assert.Contains("if (includeResidencySnapshot && foliage.IsSettled)", source);
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
        Assert.Contains("if ! quarantine_incomplete_content", source);
        Assert.DoesNotContain("quarantine_incomplete_content || true", source);
    }

    [Fact]
    public void RealDataCacheKeyIncludesTheContentReceiptSchema()
    {
        if (FindRepositoryFile(Path.Combine(".github", "workflows", "real-data.yml")) is not { } path)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("unturned-content-receipt-v2-${{ steps.content-key.outputs.key }}", source);
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
    private static string? FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "unturned-godot.sln")))
            {
                string candidate = Path.Combine(directory.FullName, relativePath);
                return File.Exists(candidate) ? candidate : null;
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
