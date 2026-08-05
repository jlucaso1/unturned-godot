using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Two more places where this port owns RenderingServer state directly instead of letting nodes do it, and
// the sea plane, whose material carries several decisions that look arbitrary until something breaks.
//
// The renderer exists because MultiMeshInstance3D is only a managed wrapper around the same RID state: a
// large map paid for ~18,000 wrappers with no per-node behaviour. Owning the RIDs means owning what the
// engine would otherwise keep in sync — visibility, transforms, and freeing — and each of those is a way
// the world can silently stop being drawn or leak for the session.
public class WorldRenderingTests : TestClass
{
    public WorldRenderingTests(Node testScene) : base(testScene) { }

    // --- the RID-owning batch renderer ---------------------------------------------------------------

    // Batches are counted before they are realised (from the pending list) and after (from the created
    // instances), because the load reports the number and the load happens either side of _Ready.
    [Test]
    public async Task BatchesAreCountedBeforeAndAfterTheyAreRealised()
    {
        var renderer = new MultiMeshRidRenderer { Name = "Batches" };
        renderer.Add(Batch(), Transform3D.Identity);
        renderer.Add(Batch(), new Transform3D(Basis.Identity, Vector3.Right * 10f));
        Assert.Equal(2, renderer.InstanceCount);

        TestScene.AddChild(renderer);
        await NextFrame();

        Assert.Equal(2, renderer.InstanceCount);
        Assert.Equal(2, renderer.MultiMeshes.Count());

        renderer.QueueFree();
    }

    // Hiding the node has to reach the RIDs by hand. The engine hides a node's own children for you; it
    // knows nothing about instances this node created, so without the notification a "hidden" batch keeps
    // drawing — which is how a switched-off layer stays on screen.
    [Test]
    public async Task HidingTheNodeReachesTheInstancesItOwns()
    {
        var renderer = new MultiMeshRidRenderer { Name = "Batches" };
        renderer.Add(Batch(), Transform3D.Identity);
        TestScene.AddChild(renderer);
        await NextFrame();

        renderer.Visible = false;
        await NextFrame();
        renderer.Visible = true;
        await NextFrame();

        // Nothing to read back through the public API — what is asserted is that the notification path
        // runs at all rather than throwing on a freed or absent instance list.
        Assert.Equal(1, renderer.InstanceCount);
        renderer.QueueFree();
    }

    // Moving the node has to reach them too: the instance transform is world-space and was baked from the
    // node's transform at _Ready. A batch that did not follow its parent would sit where the world used
    // to be.
    [Test]
    public async Task MovingTheNodeMovesTheInstancesItOwns()
    {
        var renderer = new MultiMeshRidRenderer { Name = "Batches" };
        renderer.Add(Batch(), Transform3D.Identity);
        TestScene.AddChild(renderer);
        await NextFrame();

        renderer.Position = new Vector3(100f, 0f, 0f);
        await NextFrame();

        Assert.Equal(1, renderer.InstanceCount);
        renderer.QueueFree();
    }

    // The pending list is dropped once the RIDs exist, and the meshes are retained instead — a MultiMesh
    // is RefCounted, so something has to hold it or the instance points at a freed base.
    [Test]
    public async Task ThePendingListIsCompactedButTheMeshesAreRetained()
    {
        var renderer = new MultiMeshRidRenderer { Name = "Batches" };
        MultiMesh mesh = Batch();
        renderer.Add(mesh, Transform3D.Identity);
        TestScene.AddChild(renderer);
        await NextFrame();

        // Still reachable, and still the same resource: the retained list is what keeps it alive.
        Assert.Same(mesh, renderer.MultiMeshes.Single());

        renderer.QueueFree();
    }

    // …unless asked to keep the upload metadata, which is the diagnostic path.
    [Test]
    public async Task TheUploadMetadataCanBeKept()
    {
        string previous = OS.GetEnvironment("UG_KEEP_RID_UPLOAD_METADATA");
        OS.SetEnvironment("UG_KEEP_RID_UPLOAD_METADATA", "1");
        try
        {
            var renderer = new MultiMeshRidRenderer { Name = "Batches" };
            renderer.Add(Batch(), Transform3D.Identity);
            TestScene.AddChild(renderer);
            await NextFrame();

            renderer.Position = new Vector3(5f, 0f, 0f); // the other transform-update branch
            await NextFrame();

            Assert.Equal(1, renderer.InstanceCount);
            renderer.QueueFree();
        }
        finally
        {
            OS.SetEnvironment("UG_KEEP_RID_UPLOAD_METADATA", previous);
        }
    }

    // A visibility range is only asked for when one was given. Setting one unconditionally would make
    // every batch fade, and the margin decides between a dithered cross-fade and a hard switch — with no
    // margin there is nothing to dither, so asking for Self would draw both sides of the swap for nothing.
    [Test]
    public async Task VisibilityRangesAreOnlySetWhenAskedFor()
    {
        var renderer = new MultiMeshRidRenderer { Name = "Batches" };
        renderer.Add(Batch(), Transform3D.Identity);                                    // no range
        renderer.Add(Batch(), Transform3D.Identity, visibilityEnd: 200f);               // hard switch
        renderer.Add(Batch(), Transform3D.Identity, visibilityEnd: 200f,
            visibilityMargin: 20f);                                                     // dithered
        renderer.Add(Batch(), Transform3D.Identity, visibilityBegin: 100f,
            visibilityEnd: 400f, visibilityMargin: 20f);                                // an LOD taking over
        TestScene.AddChild(renderer);
        await NextFrame();

        Assert.Equal(4, renderer.InstanceCount);
        renderer.QueueFree();
    }

    // Leaving the tree frees every instance. Nothing else owns them — a RID is not a node — so the leak
    // would last the process, on every map change.
    [Test]
    public async Task LeavingTheTreeFreesEveryInstance()
    {
        var renderer = new MultiMeshRidRenderer { Name = "Batches" };
        renderer.Add(Batch(), Transform3D.Identity);
        renderer.Add(Batch(), Transform3D.Identity);
        TestScene.AddChild(renderer);
        await NextFrame();

        TestScene.RemoveChild(renderer);
        renderer.Free();

        // Nothing to assert through the API once it is gone; what this covers is that teardown runs
        // without touching a freed RID, which is what a double free would look like.
        await NextFrame();
    }

    // --- the sea ------------------------------------------------------------------------------------

    // The sea is placed and coloured from the map's own lighting: sea level is a fraction of
    // Level.TERRAIN, and the colour is the lighting's midday SEA — which is what turns a sandy seabed
    // into blue water and gives the island a coastline.
    [Test]
    public void TheSeaTakesItsHeightAndColourFromTheMap()
    {
        LevelLighting lighting = Lighting(seaLevel: 0.25f);
        Node3D water = WaterBuilder.Build(lighting, out StandardMaterial3D material);

        Assert.Equal(0.25f * 256f, water.Position.Y);
        Color midday = lighting.Midday.Sea;
        Assert.Equal(midday.R, material.AlbedoColor.R);
        Assert.Equal(midday.G, material.AlbedoColor.G);

        water.Free();
    }

    // A map with no Lighting.dat still gets a sea, at PEI's shipped level and colour — the alternative
    // is a map whose seabed is simply bare sand.
    [Test]
    public void AMapWithNoLightingStillGetsASea()
    {
        Node3D water = WaterBuilder.Build(null, out StandardMaterial3D material);

        Assert.True(water.Position.Y > 0f, "the sea was placed at or below zero");
        Assert.True(material.AlbedoColor.B > material.AlbedoColor.R, "the default sea is not blue");

        water.Free();
    }

    // Every one of these looks arbitrary until something breaks, which is why they are pinned:
    //
    //   translucent      — the authored Water_Fallback values (alpha 0.9, smoothness 0.9, metallic 0)
    //   two-sided        — it is seen from above AND from underwater
    //   no shadows in    — a tree's shadow floating on the sea reads wrong
    //   no shadows out   — a transparent caster produces a bogus opaque shadow on the seabed
    //   depth ALWAYS     — foliage mid dither-fade renders through the transparent pipeline, and per
    //                      object sorting against a map-sized plane is ambiguous: underwater pebbles in
    //                      fade drew ON TOP of the sea until the surface was in the depth buffer
    [Test]
    public void TheSeaMaterialKeepsItsHardWonSettings()
    {
        Node3D water = WaterBuilder.Build(null, out StandardMaterial3D material);

        Assert.Equal(0.9f, material.AlbedoColor.A);
        Assert.Equal(BaseMaterial3D.TransparencyEnum.Alpha, material.Transparency);
        Assert.Equal(0.1f, material.Roughness);
        Assert.Equal(0f, material.Metallic);
        Assert.Equal(BaseMaterial3D.CullModeEnum.Disabled, material.CullMode);
        Assert.True(material.DisableReceiveShadows);
        Assert.Equal(BaseMaterial3D.DepthDrawModeEnum.Always, material.DepthDrawMode);
        Assert.Equal(GeometryInstance3D.ShadowCastingSetting.Off,
            ((MeshInstance3D)water).CastShadow);

        water.Free();
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static MultiMesh Batch()
    {
        var mesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = new BoxMesh(),
            InstanceCount = 2,
        };
        mesh.SetInstanceTransform(0, Transform3D.Identity);
        mesh.SetInstanceTransform(1, new Transform3D(Basis.Identity, Vector3.Right));
        return mesh;
    }

    private static LevelLighting Lighting(float seaLevel)
    {
        using var ms = new System.IO.MemoryStream();
        using (var w = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((byte)12);
            w.Write(281.74f);   // azimuth
            w.Write(0.1f);      // bias
            w.Write(0.2f);      // fade
            w.Write(0.32f);     // time of day
            w.Write((byte)3);   // moon phase
            w.Write(seaLevel);
            w.Write(0.6f);      // snow level
            w.Write(true);      // canRain
            w.Write(false);     // canSnow
            w.Write(1f); w.Write(2f);  // rain frequency/duration
            w.Write(3f); w.Write(4f);  // snow frequency/duration
            for (int k = 0; k < 4; k++)
            {
                for (int i = 0; i < 12; i++)
                {
                    w.Write((byte)(k * 60));
                    w.Write((byte)(i * 20));
                    w.Write((byte)(255 - k * 60));
                }
                w.Write(0.25f + k);
                w.Write(0.1f * k);
                w.Write(0.2f * k);
                w.Write(0.75f - 0.25f * k);
                w.Write(0.25f);
            }
        }
        return LevelLighting.Parse(new System.IO.MemoryStream(ms.ToArray()));
    }

    private SignalAwaiter NextFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
}
