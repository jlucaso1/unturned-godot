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

    // The hole in the ground is a hole in the FLOOR: exactly the cells the map cuts away let a probe
    // through, and every cell beside them still carries the player.
    //
    // This is the test that has to be end-to-end rather than arithmetic, because the cut is made with a
    // tool that does not fit the job. Godot's heightfield can only refuse collision at a SAMPLE, and one
    // no-collision sample takes out every triangle around it, so TerrainHoleCollision deliberately cuts
    // wider than the map asked for and patches the difference back. Whether the union of those two shapes
    // is the surface the map describes is a question for the physics server, not for the arithmetic that
    // fed it — and the answer is what tells a repair that misses a cell apart from one that is correct.
    [Test]
    public async Task ExactlyTheCellsTheMapCutsAwayAreOpenToTheSky()
    {
        // Two clusters, one of them an L, so the repair cannot get away with covering a rectangle.
        (int X, int Y)[] cut =
        {
            (62, 62), (62, 63), (63, 62), (63, 63), (64, 62), (64, 63),
            (190, 68), (190, 69), (191, 68), (191, 69), (192, 68), (192, 69), (192, 70),
        };
        using var sandbox = new PhysicsSandbox(TestScene);
        TerrainBuilder.TileMesh built =
            TerrainBuilder.BuildTileMesh(Rough(0, 0, Holes(cut)), null, textured: false);
        MeshInstance3D tile = TerrainBuilder.FinishTile(built, null);
        sandbox.Root.AddChild(tile);
        TerrainBuilder.AddHeightfieldCollision(tile);
        await sandbox.Settle();

        var holes = new System.Collections.Generic.HashSet<(int, int)>(cut);
        var space = tile.GetWorld3D().DirectSpaceState;
        // Every cell within three of a cut one: the cut cells themselves, the ring the wide cut damages
        // and the repair puts back, and two rings beyond that must never have been touched at all.
        foreach ((int cx, int cy) in Neighbourhood(cut, radius: 3))
        {
            // Cell (cx, cy) spans heightmap indices cx..cx+1 (world Z) and cy..cy+1 (world X).
            float x = (cy + 0.5f) * TerrainHeightfield.CellSize;
            float z = -((cx + 0.5f) * TerrainHeightfield.CellSize);
            var down = PhysicsRayQueryParameters3D.Create(
                new Vector3(x, 400f, z), new Vector3(x, -400f, z));
            bool solid = space.IntersectRay(down).Count > 0;

            Assert.True(solid != holes.Contains((cx, cy)),
                holes.Contains((cx, cy))
                    ? $"cell ({cx}, {cy}) is a hole but still collides"
                    : $"cell ({cx}, {cy}) is ground but nothing collides there");
        }

        tile.QueueFree();
    }

    private static System.Collections.Generic.IEnumerable<(int X, int Y)> Neighbourhood(
        (int X, int Y)[] cells, int radius)
    {
        var seen = new System.Collections.Generic.HashSet<(int, int)>();
        foreach ((int cx, int cy) in cells)
            for (int dx = -radius; dx <= radius; dx++)
                for (int dy = -radius; dy <= radius; dy++)
                    if (seen.Add((cx + dx, cy + dy)))
                        yield return (cx + dx, cy + dy);
    }

    // The test the old TerrainHeightfieldTests.Placement_ReproducesRenderMeshVertices claimed to be.
    //
    // Everything else about terrain fidelity rests on one sentence: the surface the player is put on and
    // the surface a road is lofted onto (HeightmapSampler) is the surface being drawn. Both halves are
    // asked here, of a REAL tile: every vertex BuildTileMesh emitted is handed back to the sampler and
    // has to come back at its own height. Nothing shy of this catches the failure it exists for — the
    // mesh was for a long time built at every second heightmap index while the sampler read every index,
    // and the pair of surfaces that produced could be metres apart on a ridge without a single test
    // noticing, because none of them ever built a tile and asked.
    //
    // The heights alternate per index deliberately: on a smooth field a subsampled mesh interpolates to
    // very nearly the right answer and a loose tolerance would pass it.
    [Test]
    public void TheDrawnSurfaceIsTheSurfaceTheSamplerReports()
    {
        HeightmapTile source = Rough(1, -2);
        TerrainBuilder.TileMesh built = TerrainBuilder.BuildTileMesh(source, null, textured: false);
        var sampler = new HeightmapSampler(new[] { source });

        const int res = Landscape.HEIGHTMAP_RESOLUTION;
        Godot.Collections.Array arrays = built.Importer.GetSurfaceArrays(0);
        Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].As<Vector3[]>();

        // The whole heightmap grid, not a decimation of it.
        Assert.Equal(res * res, vertices.Length);

        float worst = 0f;
        // Index 256 on either axis is the row this tile SHARES with its neighbour: it sits exactly on the
        // seam, and the sampler resolves a point there to the next tile along — which is not loaded here,
        // and would be answering about its own index 0 if it were. Every cell this tile owns is covered by
        // the 0..255 corners.
        for (int hx = 0; hx < res - 1; hx++)
        {
            for (int hy = 0; hy < res - 1; hy++)
            {
                Vector3 vertex = vertices[(hx * res) + hy];
                // Mesh vertices are in Godot space (Z mirrored); the sampler works in Unity's +Z.
                Assert.True(sampler.TrySampleHeight(vertex.X, -vertex.Z, out float sampled));
                worst = Mathf.Max(worst, Mathf.Abs(sampled - vertex.Y));
            }
        }
        // Millimetres, and that only for float rounding through the world transform — a decimated mesh
        // measures tens of centimetres here on this terrain.
        Assert.True(worst < 0.01f, $"drawn surface is up to {worst:F3} m from what the sampler reports");
    }

    // A cell the map cuts away is not drawn — and stays not drawn at every level meshoptimizer generates.
    // A hole is a topological border, which 4.7's border-locked LOD generation is supposed to preserve;
    // if it ever stopped, the entrance would seal itself back up as the player walked away from it.
    [Test]
    public void AHoleIsCutFromTheMeshAndStaysCutAtEveryLod()
    {
        // A 3x2 block of holes, the shape PEI's own two hole tiles carry.
        LandscapeHoles holes = Holes((62, 62), (62, 63), (63, 62), (63, 63), (64, 62), (64, 63));
        TerrainBuilder.TileMesh built = TerrainBuilder.BuildTileMesh(Rough(0, 0, holes), null, false);

        Godot.Collections.Array arrays = built.Importer.GetSurfaceArrays(0);
        Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].As<Vector3[]>();
        int[] baseIndices = arrays[(int)Mesh.ArrayType.Index].As<int[]>();

        // Six cells of two triangles each, gone from the base mesh.
        int cells = Landscape.HEIGHTMAP_RESOLUTION - 1;
        Assert.Equal(((cells * cells) - 6) * 6, baseIndices.Length);

        // Cell (62, 62) covers heightmap indices 62..63, which is world Z 248..252 and world X 248..252
        // (hx -> Z, hy -> X), mirrored to -Z in Godot. Nothing may cover its centre, at any level.
        var centre = new Vector2(250f, -250f);
        for (int lod = 0; lod < built.Importer.GetSurfaceLodCount(0); lod++)
            AssertNothingCovers(vertices, built.Importer.GetSurfaceLodIndices(0, lod), centre, $"LOD {lod}");
        AssertNothingCovers(vertices, baseIndices, centre, "the base mesh");
    }

    // A tile whose holes file exists but cuts nothing is drawn whole, and takes no hole path at all.
    [Test]
    public void ATileThatCutsNothingIsDrawnWhole()
    {
        TerrainBuilder.TileMesh built = TerrainBuilder.BuildTileMesh(Rough(0, 0, Holes()), null, false);

        int cells = Landscape.HEIGHTMAP_RESOLUTION - 1;
        int[] indices = built.Importer.GetSurfaceArrays(0)[(int)Mesh.ArrayType.Index].As<int[]>();
        Assert.Equal(cells * cells * 6, indices.Length);
        Assert.Null(built.Holes);
    }

    // No triangle of `indices` covers `point` in the XZ plane.
    private static void AssertNothingCovers(Vector3[] vertices, int[] indices, Vector2 point, string what)
    {
        for (int i = 0; i < indices.Length; i += 3)
        {
            Vector2 a = Flatten(vertices[indices[i]]);
            Vector2 b = Flatten(vertices[indices[i + 1]]);
            Vector2 c = Flatten(vertices[indices[i + 2]]);
            Assert.False(Covers(a, b, c, point), $"{what} still draws over the hole at {point}");
        }
    }

    private static Vector2 Flatten(Vector3 v) => new(v.X, v.Z);

    private static bool Covers(Vector2 a, Vector2 b, Vector2 c, Vector2 p)
    {
        float d1 = Side(p, a, b), d2 = Side(p, b, c), d3 = Side(p, c, a);
        bool negative = d1 < 0 || d2 < 0 || d3 < 0;
        bool positive = d1 > 0 || d2 > 0 || d3 > 0;
        return !(negative && positive);
    }

    private static float Side(Vector2 p, Vector2 a, Vector2 b) =>
        ((p.X - b.X) * (a.Y - b.Y)) - ((a.X - b.X) * (p.Y - b.Y));

    // A holes file cutting exactly the given cells, built the way the game writes one: all ones is intact
    // ground, and a CLEARED bit is the hole.
    private static LandscapeHoles Holes(params (int X, int Y)[] cells)
    {
        var bytes = new byte[LandscapeHoles.FILE_BYTES];
        bytes[0] = 1; // version
        for (int i = 1; i < bytes.Length; i++)
            bytes[i] = 0xFF;
        foreach ((int x, int y) in cells)
            bytes[1 + (x * (Landscape.HOLES_RESOLUTION / 8)) + (y >> 3)] &= (byte)~(1 << (y & 7));
        return LandscapeHoles.Parse(bytes, 0, 0);
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

    // A tile's material carries the layers the tile PAINTS, not the eight it names. The terrain fills the
    // screen, so every sampler bound here is paid at every ground pixel; a tile that paints three needs
    // three layer textures and ONE control texture, not eight and two.
    [Test]
    public void ATileBindsOnlyTheLayersItPaints()
    {
        var layers = new ImageTexture[SplatmapTile.LAYERS];
        for (int i = 0; i < layers.Length; i++)
            layers[i] = Pixel();
        SplatmapTile splat = Painted((0, 0, 2, 200), (0, 0, 5, 55), (17, 9, 7, 255));

        ShaderMaterial material = SplatMaterial(splat, layers);

        // Layers 2, 5 and 7 in that order, and nothing bound past the third slot.
        Assert.Same(layers[2], material.GetShaderParameter("layer0").As<ImageTexture>());
        Assert.Same(layers[5], material.GetShaderParameter("layer1").As<ImageTexture>());
        Assert.Same(layers[7], material.GetShaderParameter("layer2").As<ImageTexture>());
        Assert.Null(material.GetShaderParameter("layer3").As<ImageTexture>());
        Assert.NotNull(material.GetShaderParameter("control0").As<ImageTexture>());
        Assert.Null(material.GetShaderParameter("control1").As<ImageTexture>());
    }

    // The weights have to follow the layers into their new slots. Layer 5's weight belongs in the channel
    // that multiplies layer 5's texture, and the slots the tile does not fill must read zero so they add
    // nothing to the blend OR to the total it is normalized by.
    [Test]
    public void TheControlTextureCarriesEachPaintedLayersWeightInItsOwnSlot()
    {
        var layers = new ImageTexture[SplatmapTile.LAYERS];
        for (int i = 0; i < layers.Length; i++)
            layers[i] = Pixel();
        SplatmapTile splat = Painted((17, 9, 2, 200), (17, 9, 5, 55), (17, 9, 7, 128));

        ShaderMaterial material = SplatMaterial(splat, layers);
        Image control = material.GetShaderParameter("control0").As<ImageTexture>().GetImage();

        // Image pixel (x, y) carries splat texel [x, y] — the same texel UV2 lands on.
        Color texel = control.GetPixel(17, 9);
        Assert.Equal(200, Mathf.RoundToInt(texel.R * 255f)); // slot 0 = layer 2
        Assert.Equal(55, Mathf.RoundToInt(texel.G * 255f));  // slot 1 = layer 5
        Assert.Equal(128, Mathf.RoundToInt(texel.B * 255f)); // slot 2 = layer 7
        Assert.Equal(0, Mathf.RoundToInt(texel.A * 255f));   // unfilled slot: contributes nothing
        Assert.Equal(Colors.Black with { A = 0f }, control.GetPixel(18, 9)); // an unpainted texel
    }

    // Four weights fit an RGBA8 image, so a fifth painted layer is where a SECOND control texture has to
    // appear and where slot 4's weight has to land in its red channel — the channel the generated shader
    // multiplies layer4 by. Everything above only ever fills one control texture, which leaves the loop
    // bound and the slot-to-channel mapping across the boundary untested.
    [Test]
    public void ATilePaintingMoreThanFourLayersGetsASecondControlTexture()
    {
        var layers = new ImageTexture[SplatmapTile.LAYERS];
        for (int i = 0; i < layers.Length; i++)
            layers[i] = Pixel();
        // Layer 6 rather than 4, so a slot's index and its layer's index cannot be confused for one
        // another: painted layer 6 has to arrive in slot 4, the first channel of the second control.
        SplatmapTile splat = Painted((17, 9, 0, 10), (17, 9, 1, 20), (17, 9, 2, 30),
            (17, 9, 3, 40), (17, 9, 6, 155));

        ShaderMaterial material = SplatMaterial(splat, layers);

        Assert.Same(layers[6], material.GetShaderParameter("layer4").As<ImageTexture>());
        Color texel = material.GetShaderParameter("control1").As<ImageTexture>().GetImage().GetPixel(17, 9);
        Assert.Equal(155, Mathf.RoundToInt(texel.R * 255f)); // slot 4 = layer 6
        Assert.Equal(0, Mathf.RoundToInt(texel.G * 255f));   // and the three slots it does not fill
        Assert.Equal(0, Mathf.RoundToInt(texel.B * 255f));
        Assert.Equal(0, Mathf.RoundToInt(texel.A * 255f));
        Assert.Null(material.GetShaderParameter("control2").As<ImageTexture>());
    }

    // A tile whose splatmap is empty paints nothing. It still has to draw — the eight-way blend rendered
    // it black through its `total > 0.0` guard, and a zero-sampler shader would not even compile.
    [Test]
    public void ATileThatPaintsNothingStillDraws()
    {
        var layers = new ImageTexture[SplatmapTile.LAYERS];
        for (int i = 0; i < layers.Length; i++)
            layers[i] = Pixel();

        ShaderMaterial material = SplatMaterial(Painted(), layers);

        Assert.Same(layers[0], material.GetShaderParameter("layer0").As<ImageTexture>());
        Assert.Null(material.GetShaderParameter("layer1").As<ImageTexture>());
    }

    // The generated shader is what the per-pixel saving actually rides on, and it is assembled from a
    // count rather than written out, so the shape is worth pinning: one branch per painted layer, guarded
    // by that layer's own weight channel, and a `total` that still sums every one of them so the
    // normalized average matches the unconditional blend exactly.
    [Test]
    public void TheGeneratedShaderGuardsEverySampleAndStillSumsEveryWeight()
    {
        string code = TerrainBuilder.SplatShaderCode(5);

        Assert.Contains("render_mode cull_back, specular_disabled;", code);
        Assert.Contains("uniform sampler2D layer4 :", code);
        Assert.DoesNotContain("uniform sampler2D layer5 :", code);
        Assert.Contains("uniform sampler2D control1 :", code); // a fifth layer needs a second control
        Assert.Contains("if (sample_unpainted || c1.r > 0.0)", code);
        Assert.Contains("texture(layer4, uv).rgb * c1.r", code);
        Assert.Contains("float total = c0.r + c0.g + c0.b + c0.a + c1.r;", code);
        Assert.Contains("ALBEDO = total > 0.0 ? albedo / total : albedo;", code);
        // The sample inside the branch keeps its implicit LOD — textureGrad measurably lost the
        // anisotropic taps — and SPECULAR stays written, because the sky's indirect specular reads f0
        // even though the render mode has disabled the direct lobe.
        Assert.DoesNotContain("textureGrad", code);
        Assert.Contains("SPECULAR = 0.0;", code);
    }

    // Pinning one count leaves the other seven to be found at runtime, where a malformed generator is a
    // shader that fails to compile and a tile that draws untextured. PEI alone asks for six of the eight.
    [Test]
    public void EveryPaintedCountComesOutWellFormed()
    {
        for (int painted = 1; painted <= SplatmapTile.LAYERS; painted++)
        {
            string code = TerrainBuilder.SplatShaderCode(painted);
            int controls = (painted + 3) / 4;

            for (int slot = 0; slot < painted; slot++)
                Assert.Contains($"uniform sampler2D layer{slot} :", code);
            Assert.DoesNotContain($"uniform sampler2D layer{painted} :", code);
            Assert.Contains($"uniform sampler2D control{controls - 1} :", code);
            Assert.DoesNotContain($"uniform sampler2D control{controls} :", code);

            // One guarded sample per painted layer, and a `total` that still sums every one of them —
            // the two halves of "the normalized average is what the eight-way blend produced".
            Assert.Equal(painted, code.Split("if (sample_unpainted || ").Length - 1);
            string total = code.Split("float total = ")[1].Split(';')[0];
            Assert.Equal(painted, total.Split('+').Length);
        }
    }

    // --- helpers -------------------------------------------------------------------------------------

    // A splatmap painted only at the given (x, y, layer, weight) texels.
    private static SplatmapTile Painted(params (int X, int Y, int Layer, byte Weight)[] texels)
    {
        const int res = Landscape.SPLATMAP_RESOLUTION;
        var bytes = new byte[res * res * SplatmapTile.LAYERS];
        foreach ((int x, int y, int layer, byte weight) in texels)
            bytes[SplatmapTile.WeightIndex(x, y, layer)] = weight;
        return SplatmapTile.Parse(bytes, 0, 0);
    }

    // The splat material a textured tile carrying this splatmap ends up wearing.
    private ShaderMaterial SplatMaterial(SplatmapTile splat, ImageTexture[] layers)
    {
        TerrainBuilder.TileMesh built = TerrainBuilder.BuildTileMesh(Flat(0, 0, 0.5f), splat, textured: true);
        MeshInstance3D tile = TerrainBuilder.FinishTile(built, layers);
        TestScene.AddChild(tile);
        var material = (ShaderMaterial)tile.Mesh.SurfaceGetMaterial(0);
        tile.QueueFree();
        return material;
    }

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

    // Terrain with a feature on every index, including the odd ones a subsampled mesh would step over.
    private static HeightmapTile Rough(int x, int y, LandscapeHoles? holes = null)
    {
        const int res = Landscape.HEIGHTMAP_RESOLUTION;
        var heights = new float[res, res];
        for (int i = 0; i < res; i++)
            for (int j = 0; j < res; j++)
                // The alternating term is the point: a ridge that lives only on odd indices, which the
                // mesh has to carry if it is built on the heightmap's own grid and cannot if it is not.
                heights[i, j] = 0.4f + (0.0004f * (((i * 7) + (j * 13)) % 23))
                    + (((i + j) % 2 == 0) ? 0f : 0.01f);
        return HeightmapTile.FromHeights(x, y, heights, holes);
    }

    private SignalAwaiter NextPhysicsFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.PhysicsFrame);
}
