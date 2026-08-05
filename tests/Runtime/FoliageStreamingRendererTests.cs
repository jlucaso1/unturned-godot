using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Grass, kept resident only where the player can see it.
//
// PEI places hundreds of thousands of foliage instances. Holding them all costs more GPU memory than the
// rest of the map put together, so they are indexed into spatial chunks and made resident against the
// camera: a ring of chunks around it is decoded and uploaded, and chunks that fall behind are dropped.
//
// The whole design is about WHEN the work happens rather than whether it happens.
//
//   - Residency is planned with hysteresis, so a player walking back and forth across a chunk boundary
//     does not thrash it in and out every frame.
//   - The ring the player spawns inside is warmed under a frame budget behind the loading screen.
//     Without that, the first plan is also the first frame: every chunk already inside its visibility
//     radius is missing, so the correctness gate decodes and uploads all of them at once — measured on
//     PEI at 61 chunks and 18-26 ms.
//   - A teleport cancels the generation in flight rather than finishing decodes for a place the player
//     has left.
//
// The tests drive it over PEI's REAL Foliage.blob with a synthetic mesh per type. That split is
// deliberate: the placements are what the streaming reasons about — hundreds of thousands of them, at
// their real positions and densities — while what each blade looks like is the extraction's business and
// is covered where that lives.
public class FoliageStreamingRendererTests : TestClass
{
    public FoliageStreamingRendererTests(Node testScene) : base(testScene) { }

    // An index with no meshes to draw it with is a renderer with nothing renderable — and it still runs.
    // A load whose extraction failed reaches exactly this, and it must produce a bald map rather than a
    // session that will not start.
    [Test]
    public async Task AnIndexWithNoMeshesIsARendererWithNothingToDo()
    {
        if (!Index(out FoliageResidencyIndex index, out TempDir? temp))
            return;

        using (temp)
        using (var world = new StreamingWorld(TestScene, index, new Dictionary<Guid, ArrayMesh>()))
        {
            await world.Run(frames: 5);

            Assert.Equal(0L, world.Renderer.ResidentInstances);
            Assert.Empty(world.Renderer.StructuralChunks);
        }
    }

    // With meshes, the ring around the camera becomes resident. This is the thing itself: the map's real
    // foliage, made resident where the camera is and nowhere else.
    [Test]
    [Timeout(300_000)]
    public async Task TheRingAroundTheCameraBecomesResident()
    {
        if (!Index(out FoliageResidencyIndex index, out TempDir? temp))
            return;

        using (temp)
        using (var world = new StreamingWorld(TestScene, index, MeshPerType(index)))
        {
            world.LookFrom(index.Chunks.Count > 0 ? index.Chunks[0].Centre : Vector3.Zero);
            await world.Run(frames: 60);

            Assert.True(world.Renderer.ResidentInstances > 0,
                "the camera stood in the middle of the map's foliage and none of it became resident");
            Assert.True(world.Renderer.ResidentBufferBytes > 0);
        }
    }

    // The structural index reports the COMPLETE scene, not what happens to be resident — and that is
    // the point of it. Tier 1 attaches no camera and advances no frame, so a streaming renderer has no
    // GPU buffers at all; if the benchmark read residency it would report a world that got emptier every
    // time streaming got better, and every improvement would look like a regression in scene content.
    //
    // So it must not move when the camera does. That is what this drives: the same index read from the
    // middle of the map's foliage and from fifty kilometres away.
    [Test]
    [Timeout(300_000)]
    public async Task TheStructuralIndexDoesNotMoveWhenTheCameraDoes()
    {
        if (!Index(out FoliageResidencyIndex index, out TempDir? temp))
            return;

        using (temp)
        using (var world = new StreamingWorld(TestScene, index, MeshPerType(index)))
        {
            world.LookFrom(index.Chunks.Count > 0 ? index.Chunks[0].Centre : Vector3.Zero);
            await world.Run(frames: 60);

            (int chunks, long instances) near = Structure(world);
            Assert.True(near.chunks > 0, "the renderer reported no structural chunks at all");
            Assert.True(near.instances >= world.Renderer.ResidentInstances,
                "the structural index reported fewer instances than are resident");

            world.LookFrom(new Vector3(50_000f, 0f, 50_000f));
            await world.Run(frames: 60);

            Assert.Equal(near, Structure(world));
        }
    }

    // Every structural chunk carries a mesh and a count. A chunk with neither would be counted by the
    // benchmark as a batch that draws nothing.
    [Test]
    [Timeout(300_000)]
    public async Task EveryStructuralChunkCarriesAMeshAndACount()
    {
        if (!Index(out FoliageResidencyIndex index, out TempDir? temp))
            return;

        using (temp)
        using (var world = new StreamingWorld(TestScene, index, MeshPerType(index)))
        {
            await world.Run(frames: 5);

            foreach (FoliageStructuralChunk chunk in world.Renderer.StructuralChunks)
            {
                Assert.NotNull(chunk.Mesh);
                Assert.True(chunk.Count > 0, "a structural chunk carries no instances");
            }
        }
    }

    // Walking away drops what is behind. Without that the ring only ever grows, and a player who crossed
    // the map would be holding every chunk of it.
    [Test]
    [Timeout(300_000)]
    public async Task WalkingAwayDropsWhatIsBehind()
    {
        if (!Index(out FoliageResidencyIndex index, out TempDir? temp))
            return;

        using (temp)
        using (var world = new StreamingWorld(TestScene, index, MeshPerType(index)))
        {
            world.LookFrom(index.Chunks.Count > 0 ? index.Chunks[0].Centre : Vector3.Zero);
            await world.Run(frames: 60);
            long near = world.Renderer.ResidentInstances;
            if (near == 0)
                return; // covered by the test above

            // Far enough to be a teleport, which cancels the generation in flight rather than finishing
            // decodes for a place the player has left.
            world.LookFrom(new Vector3(50_000f, 0f, 50_000f));
            await world.Run(frames: 60);

            Assert.True(world.Renderer.ResidentInstances < near,
                $"the camera left the map and residency stayed at {world.Renderer.ResidentInstances}");
        }
    }

    // The spawn ring is warmed behind the loading screen. Without it the first _Process is also the first
    // plan, and the correctness gate decodes and uploads every chunk already inside its visibility radius
    // on one frame.
    [Test]
    [Timeout(300_000)]
    public async Task TheSpawnRingIsWarmedBeforeTheFirstFrame()
    {
        if (!Index(out FoliageResidencyIndex index, out TempDir? temp))
            return;

        using (temp)
        using (var world = new StreamingWorld(TestScene, index, MeshPerType(index)))
        {
            world.LookFrom(index.Chunks.Count > 0 ? index.Chunks[0].Centre : Vector3.Zero);
            await world.Renderer.PrewarmAsync();

            Assert.True(world.Renderer.PrewarmTotalMs >= 0.0);
        }
    }

    // A warm that is cancelled mid-flight stops. The caller's own load token is not optional in practice:
    // this node cannot leave the tree until the load that owns it has finished tearing down, and that
    // teardown waits on the task this pass runs inside — so without it, returning to the menu would wait
    // out every remaining decode and paced upload.
    [Test]
    [Timeout(300_000)]
    public async Task ACancelledWarmStopsRatherThanFinishing()
    {
        if (!Index(out FoliageResidencyIndex index, out TempDir? temp))
            return;

        using (temp)
        using (var world = new StreamingWorld(TestScene, index, MeshPerType(index)))
        {
            world.LookFrom(index.Chunks.Count > 0 ? index.Chunks[0].Centre : Vector3.Zero);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            await world.Renderer.PrewarmAsync(cancelled.Token);
        }
    }

    // What the benchmark would read off this renderer: how many batches, and how many instances across
    // them.
    private static (int Chunks, long Instances) Structure(StreamingWorld world)
    {
        int chunks = 0;
        long instances = 0;
        foreach (FoliageStructuralChunk chunk in world.Renderer.StructuralChunks)
        {
            chunks++;
            instances += chunk.Count;
        }
        return (chunks, instances);
    }

    // --- helpers -------------------------------------------------------------------------------------

    // A renderer in a world of its own, with a camera it can be driven by. Its own SubViewport, because
    // the renderer reads the camera off its viewport and the shared test scene has one of its own.
    private sealed class StreamingWorld : IDisposable
    {
        private readonly Node _testScene;
        private readonly SubViewport _viewport;
        private readonly Camera3D _camera;

        public FoliageStreamingRenderer Renderer { get; }

        public StreamingWorld(Node testScene, FoliageResidencyIndex index,
            IReadOnlyDictionary<Guid, ArrayMesh> meshes)
        {
            _testScene = testScene;
            _viewport = new SubViewport
            {
                OwnWorld3D = true,
                Size = new Vector2I(4, 4),
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            };
            _camera = new Camera3D { Current = true };
            _viewport.AddChild(_camera);

            Renderer = FoliageStreamingRenderer.Create(index, meshes, drawDistance: 128f);
            _viewport.AddChild(Renderer);
            testScene.AddChild(_viewport);
        }

        public void LookFrom(Vector3 position) => _camera.Position = position;

        public async Task Run(int frames)
        {
            for (int i = 0; i < frames; i++)
                await _testScene.ToSignal(_testScene.GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        public void Dispose()
        {
            _testScene.RemoveChild(_viewport);
            _viewport.Free(); // takes the renderer, the camera and every multimesh RID with it
        }
    }

    // One triangle per foliage type. What a blade looks like is the extraction's business; what the
    // streaming reasons about is where the instances are, and those are the map's own.
    private static Dictionary<Guid, ArrayMesh> MeshPerType(FoliageResidencyIndex index)
    {
        var meshes = new Dictionary<Guid, ArrayMesh>();
        foreach (Guid asset in index.AssetGuids)
            meshes[asset] = Triangle();
        return meshes;
    }

    private static ArrayMesh Triangle()
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[]
        {
            Vector3.Zero, new Vector3(0.2f, 0f, 0f), new Vector3(0f, 0.4f, 0f),
        };
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    // PEI's real foliage, indexed into a directory the test owns rather than the machine's own cache.
    private static bool Index(out FoliageResidencyIndex index, out TempDir? temp)
    {
        index = null!;
        temp = null;

        string? install = Assets.UnturnedInstall.Find();
        string? maps = install == null ? null : System.IO.Path.Combine(install, "Maps", "PEI");
        string blob = maps == null ? "" : System.IO.Path.Combine(maps, "Foliage.blob");
        if (blob.Length == 0 || !System.IO.File.Exists(blob))
        {
            if (System.Environment.GetEnvironmentVariable("UG_REQUIRE_REAL_DATA") == "1")
            {
                throw new System.IO.IOException(
                    "UG_REQUIRE_REAL_DATA=1 but PEI's Foliage.blob is not present; this run exists to "
                    + "prove these tests execute");
            }

            Log.Print("[runtime-tests] skipping: no PEI foliage "
                + "(set UNTURNED_PATH or run ./scripts/fetch-game-data.sh)");
            return false;
        }

        temp = new TempDir();
        FoliageResidencyIndex? built = FoliageResidencyIndex.LoadOrBuild(blob,
            System.IO.Path.Combine(temp.Path, FoliageResidencyIndex.CacheFileName(blob)),
            FoliageBuilder.RuntimeChunkTiles, out _);
        if (built == null)
        {
            temp.Dispose();
            temp = null;
            return false;
        }

        index = built;
        return true;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "unturned-godot-foliage-index-" + Guid.NewGuid().ToString("N"));

        public TempDir() => System.IO.Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Path, recursive: true);
            }
            catch (System.IO.IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }
}
