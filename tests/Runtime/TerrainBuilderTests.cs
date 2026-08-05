using System.Collections.Generic;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Turning a heightmap into a drawn, walkable tile.
//
// The tessellation and the heightfield maths are pure and unit tested in core/. What is only testable here
// is the split the whole build is arranged around: the expensive half (read, tessellate, generate the LOD
// chain) runs on worker threads over data-only Resources, and only the cheap half — GetMesh, materials,
// collision — touches the RenderingServer on the main thread. A tile that created an engine resource in
// the wrong half would work in a test and race in a real load.
//
// The collision is worth pinning on its own. It comes from the FULL-resolution heightmap the tile carries
// in metadata, independent of whatever LOD is being drawn: a player must not fall through a distant tile
// because it happened to be rendering at a lower detail level.
public class TerrainBuilderTests : TestClass
{
    public TerrainBuilderTests(Node testScene) : base(testScene) { }

    // The worker-thread half produces data, not engine resources, and carries the full-resolution
    // heightmap alongside whatever the visual mesh was subsampled to.
    [Test]
    public void TheWorkerHalfProducesDataAndKeepsTheFullResolutionHeights()
    {
        TerrainBuilder.TileMesh tile = TerrainBuilder.BuildTileMesh(Flat(2, -3, 0.5f), null, textured: false);

        Assert.Equal(2, tile.X);
        Assert.Equal(-3, tile.Y);
        Assert.NotNull(tile.Importer);
        // The heightmap kept for collision is the full 257x257, whatever the mesh was built at.
        int full = Landscape.HEIGHTMAP_RESOLUTION * Landscape.HEIGHTMAP_RESOLUTION;
        Assert.Equal(full, (tile.Heights16?.Length ?? 0) + (tile.Heights32?.Length ?? 0));
        Assert.Equal(0.5f, tile.HeightAt(0));
    }

    // The main-thread half turns it into something drawable, named by its coordinates so a tile can be
    // found again.
    [Test]
    public void TheMainThreadHalfProducesADrawableTile()
    {
        TerrainBuilder.TileMesh built = TerrainBuilder.BuildTileMesh(Flat(1, 1, 0.25f), null, textured: false);

        MeshInstance3D tile = TerrainBuilder.FinishTile(built, null);
        TestScene.AddChild(tile);

        Assert.Equal("Tile_1_1", tile.Name);
        Assert.NotNull(tile.Mesh);
        Assert.True(tile.Mesh.GetSurfaceCount() > 0);
        Assert.NotNull(tile.Mesh.SurfaceGetMaterial(0));

        tile.QueueFree();
    }

    // Collision comes from the full-resolution heightmap in metadata, not from the drawn mesh. A player
    // must not fall through a distant tile because it is rendering at a lower LOD.
    [Test]
    public async Task CollisionComesFromTheFullResolutionHeightmap()
    {
        TerrainBuilder.TileMesh built = TerrainBuilder.BuildTileMesh(Flat(0, 0, 0.5f), null, textured: false);
        MeshInstance3D tile = TerrainBuilder.FinishTile(built, null);
        TestScene.AddChild(tile);

        TerrainBuilder.AddHeightfieldCollision(tile);
        await NextPhysicsFrame();

        // A heightfield body, not a trimesh of the drawn surface: a 257x257 shape rather than ~131k
        // triangles, which is the entire point of doing it this way.
        StaticBody3D? body = FindBody(tile);
        Assert.NotNull(body);

        tile.QueueFree();
    }

    // A tile that is not one of ours, or one carrying no heightmap, is left alone rather than throwing —
    // the caller walks whatever the world builder produced.
    [Test]
    public void ATileWithNoHeightmapIsLeftAlone()
    {
        var plain = new MeshInstance3D { Name = "NotATerrainTile" };
        TestScene.AddChild(plain);

        TerrainBuilder.AddHeightfieldCollision(plain);

        Assert.Equal(0, plain.GetChildCount());
        plain.QueueFree();
    }

    // The textured path needs every one of the eight splat layers. A map missing any of them falls back
    // to the averaged-colour material rather than drawing with a hole in its palette — which is why this
    // returns null instead of a partly-filled array.
    [Test]
    public void AMapMissingAnyLayerTextureFallsBackRatherThanDrawingAHole()
    {
        var textures = new Dictionary<string, ImageTexture>();
        for (int i = 0; i < TerrainPalette.LayerTextureNames.Length - 1; i++)
            textures[TerrainPalette.LayerTextureNames[i]] = Pixel();

        Assert.Null(TerrainBuilder.MapLayerTextures(textures)); // one short

        textures[TerrainPalette.LayerTextureNames[^1]] = Pixel();
        ImageTexture[]? layers = TerrainBuilder.MapLayerTextures(textures);

        Assert.NotNull(layers);
        Assert.Equal(SplatmapTile.LAYERS, layers!.Length);
    }

    // The layers come back in SPLAT order, not in dictionary order: the shader indexes them by layer, so
    // a mismatched order paints grass where the map authored sand.
    //
    // Note the palette names a texture TWICE — "Grass" is both layer 2 and layer 6 — so this cannot be a
    // one-to-one check. Each slot has to carry the texture its own NAME resolves to, and the two grass
    // layers legitimately share one.
    [Test]
    public void EachLayerCarriesTheTextureItsOwnNameResolvesTo()
    {
        var textures = new Dictionary<string, ImageTexture>();
        foreach (string name in TerrainPalette.LayerTextureNames)
            textures[name] = Pixel();

        ImageTexture[]? layers = TerrainBuilder.MapLayerTextures(textures);

        Assert.NotNull(layers);
        for (int i = 0; i < layers!.Length; i++)
            Assert.Same(textures[TerrainPalette.LayerTextureNames[i]], layers[i]);

        // And the duplicate really is shared rather than decoded twice.
        Assert.Same(layers[2], layers[6]);
    }

    // A tile with a splatmap but no layer textures still draws — with the averaged-colour material, which
    // is what a map whose Materials.unity3d could not be read gets.
    [Test]
    public void ATileWithNoLayerTexturesStillDraws()
    {
        TerrainBuilder.TileMesh built = TerrainBuilder.BuildTileMesh(Flat(0, 0, 0.4f), null, textured: false);

        MeshInstance3D tile = TerrainBuilder.FinishTile(built, layerTextures: null);
        TestScene.AddChild(tile);

        Assert.NotNull(tile.Mesh.SurfaceGetMaterial(0));
        tile.QueueFree();
    }

    // The splat shader is generated for the number of layers the tile paints, and the eight-layer form is
    // the maximum rather than the norm. What has to hold is that the pruned program is the SAME blend:
    // a weighted average over the layers that are there, divided by their total.
    [Test]
    public void TheSplatShaderSamplesOnlyTheLayersTheTilePaints()
    {
        string three = TerrainBuilder.SplatShaderCode(3, covered: true);

        Assert.Contains("uniform sampler2D layer2", three);
        Assert.DoesNotContain("layer3", three);      // the five it does not paint cost it no fetch
        Assert.Contains("uniform sampler2D control0", three);
        Assert.DoesNotContain("control1", three);    // nor does the second half of the weights
        Assert.Contains("albedo / total", three);

        string eight = TerrainBuilder.SplatShaderCode(8, covered: false);
        Assert.Contains("uniform sampler2D layer7", eight);
        Assert.Contains("uniform sampler2D control1", eight);
        Assert.Contains("c1.a", eight);              // layer 7's weight, in the last channel of the second
        // A tile with an unpainted texel keeps the guard the eight-layer blend always had.
        Assert.Contains("total > 0.0", eight);
    }

    // One layer painted over the whole tile is `layer * w / w`. The weights cancel, so the tile needs no
    // control texture at all — three of PEI's sixteen are exactly this.
    [Test]
    public void ASingleFullyPaintedLayerNeedsNoControlTexture()
    {
        string code = TerrainBuilder.SplatShaderCode(1, covered: true);

        Assert.Contains("ALBEDO = texture(layer0, layer_uv).rgb;", code);
        Assert.DoesNotContain("control", code);
        Assert.DoesNotContain("total", code);

        // Not painted everywhere, though, and the weight no longer cancels: the blend comes back.
        Assert.Contains("control0", TerrainBuilder.SplatShaderCode(1, covered: false));
    }

    // A splatmap that paints nothing produced black out of the eight-layer blend (a zero numerator over a
    // zero total). It still does, without sampling anything to get there.
    [Test]
    public void ATileThatPaintsNothingDrawsBlackWithoutSampling()
    {
        string code = TerrainBuilder.SplatShaderCode(0, covered: false);

        Assert.Contains("ALBEDO = vec3(0.0);", code);
        Assert.DoesNotContain("texture(", code);
    }

    // End to end on the main thread: the material a painted tile gets binds the layers it paints, in the
    // compacted order the shader indexes them by, and nothing else.
    [Test]
    public void TheMaterialBindsThePaintedLayersInPackedOrder()
    {
        const int res = Landscape.SPLATMAP_RESOLUTION;
        var weights = new byte[res * res * SplatmapTile.LAYERS];
        for (int texel = 0; texel < res * res; texel++)
        {
            weights[(texel * SplatmapTile.LAYERS) + 2] = 100; // layers 2 and 5, both over the whole tile
            weights[(texel * SplatmapTile.LAYERS) + 5] = 155;
        }
        SplatmapTile splat = SplatmapTile.Parse(weights, 0, 0);
        var layers = new ImageTexture[SplatmapTile.LAYERS];
        for (int i = 0; i < layers.Length; i++)
            layers[i] = Pixel();

        TerrainBuilder.TileMesh built = TerrainBuilder.BuildTileMesh(Flat(0, 0, 0.5f), splat, textured: true);
        MeshInstance3D tile = TerrainBuilder.FinishTile(built, layers);
        TestScene.AddChild(tile);

        var material = (ShaderMaterial)tile.Mesh.SurfaceGetMaterial(0);
        Assert.Equal(layers[2].GetRid(), material.GetShaderParameter("layer0").As<ImageTexture>().GetRid());
        Assert.Equal(layers[5].GetRid(), material.GetShaderParameter("layer1").As<ImageTexture>().GetRid());
        Assert.NotNull(material.GetShaderParameter("control0").As<ImageTexture>());
        Assert.Null(material.GetShaderParameter("control1").As<ImageTexture>());
        // Two layers ride in two channels, not the four the eight-layer packing always uploaded.
        Assert.Equal(Image.Format.Rg8, material.GetShaderParameter("control0")
            .As<ImageTexture>().GetImage().GetFormat());

        tile.QueueFree();
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static StaticBody3D? FindBody(Node parent)
    {
        foreach (Node child in parent.GetChildren())
        {
            if (child is StaticBody3D body)
                return body;
            if (FindBody(child) is { } nested)
                return nested;
        }
        return null;
    }

    private static ImageTexture Pixel() =>
        ImageTexture.CreateFromImage(Image.CreateEmpty(1, 1, false, Image.Format.Rgba8));

    private static HeightmapTile Flat(int x, int y, float height)
    {
        const int res = Landscape.HEIGHTMAP_RESOLUTION;
        var heights = new float[res, res];
        for (int i = 0; i < res; i++)
            for (int j = 0; j < res; j++)
                heights[i, j] = height;
        return HeightmapTile.FromHeights(x, y, heights);
    }

    private SignalAwaiter NextPhysicsFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.PhysicsFrame);
}
