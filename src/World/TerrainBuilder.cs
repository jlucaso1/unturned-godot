using Godot;
using UnturnedGodot.Data;

namespace UnturnedGodot;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class TerrainBuilder
{
    // One material shared by every tile (#4): 16 identical StandardMaterial3D instances become one, so
    // the renderer batches state instead of switching per tile. Back-face culling (#3) halves fill rate
    // — the ground is only ever seen from above, so drawing its underside was wasted work. Used only as
    // the fallback when the map's real terrain textures can't be loaded.
    private static readonly StandardMaterial3D SharedMaterial = new()
    {
        VertexColorUseAsAlbedo = true,
        CullMode = BaseMaterial3D.CullModeEnum.Back,
        Roughness = 1.0f,
        // Fully-rough dielectric: the GGX specular lobe contributes nothing visible, so skip its
        // per-fragment ALU over the terrain's screen-dominating fill.
        SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
    };

    // Blends the 8 splat layers per pixel: two control textures carry the layer weights (sampled by UV2,
    // the tile-normalized position), each layer texture is tiled by world XZ, and the result is their
    // weighted average. World XZ comes straight from VERTEX since each tile mesh sits at the origin.
    private static readonly Shader SplatShader = new()
    {
        Code = """
        shader_type spatial;
        render_mode cull_back;
        uniform sampler2D layer0 : source_color, repeat_enable;
        uniform sampler2D layer1 : source_color, repeat_enable;
        uniform sampler2D layer2 : source_color, repeat_enable;
        uniform sampler2D layer3 : source_color, repeat_enable;
        uniform sampler2D layer4 : source_color, repeat_enable;
        uniform sampler2D layer5 : source_color, repeat_enable;
        uniform sampler2D layer6 : source_color, repeat_enable;
        uniform sampler2D layer7 : source_color, repeat_enable;
        uniform sampler2D control0 : repeat_disable; // weights of layers 0..3
        uniform sampler2D control1 : repeat_disable; // weights of layers 4..7
        uniform float tiling = 0.15;                 // texture repeats every 1/tiling world metres
        varying vec3 world_pos;
        void vertex() { world_pos = VERTEX; }
        void fragment() {
            vec4 c0 = texture(control0, UV2);
            vec4 c1 = texture(control1, UV2);
            vec2 uv = world_pos.xz * tiling;
            vec3 albedo =
                texture(layer0, uv).rgb * c0.r + texture(layer1, uv).rgb * c0.g +
                texture(layer2, uv).rgb * c0.b + texture(layer3, uv).rgb * c0.a +
                texture(layer4, uv).rgb * c1.r + texture(layer5, uv).rgb * c1.g +
                texture(layer6, uv).rgb * c1.b + texture(layer7, uv).rgb * c1.a;
            float total = c0.r + c0.g + c0.b + c0.a + c1.r + c1.g + c1.b + c1.a;
            ALBEDO = total > 0.0 ? albedo / total : albedo;
            ROUGHNESS = 1.0;
            SPECULAR = 0.0;
        }
        """,
    };

    // Maps the map's terrain textures (by name) onto the 8 splat layers in layer order, returning null if
    // any layer's texture is missing so the caller falls back to the averaged-color material.
    public static ImageTexture[]? MapLayerTextures(System.Collections.Generic.IReadOnlyDictionary<string, ImageTexture> textures)
    {
        var layers = new ImageTexture[SplatmapTile.LAYERS];
        for (int i = 0; i < layers.Length; i++)
        {
            if (!textures.TryGetValue(TerrainPalette.LayerTextureNames[i], out ImageTexture? tex))
                return null;
            layers[i] = tex;
        }
        return layers;
    }

    public static MeshInstance3D BuildTile(HeightmapTile tile, SplatmapTile? splat,
        ImageTexture[]? layerTextures = null)
    {
        const int res = Landscape.HEIGHTMAP_RESOLUTION;
        int vertexCount = res * res;

        // Build the mesh arrays in C# and hand them to the engine in a single AddSurfaceFromArrays call,
        // instead of SurfaceTool's per-element AddVertex/SetColor/AddIndex + GenerateNormals. The profiler
        // showed that path was ~44% of world-build time (GenerateNormals alone 34%), almost all of it
        // marshaling ~400k managed→native calls per tile. Vertex (x, y) lands at index x * res + y.
        var positions = new Vector3[vertexCount];
        var colors = new Color[vertexCount];
        var uv2 = new Vector2[vertexCount];
        float invRes = 1f / (res - 1);
        for (int x = 0; x < res; x++)
        {
            for (int y = 0; y < res; y++)
            {
                int idx = x * res + y;
                float h01 = tile.Heights[x, y];
                Vector3 unity = Landscape.GetWorldPosition(tile.CoordX, tile.CoordY, x, y, h01);
                positions[idx] = Landscape.UnityToGodot(unity);
                colors[idx] = TerrainColor.ForVertex(splat, x, y, unity.Y);
                uv2[idx] = new Vector2(x * invRes, y * invRes); // tile-normalized, for the splat control lookup
            }
        }

        // Front faces point up (verified on screen): back-face culling (#3) then drops the underside.
        var indices = new int[(res - 1) * (res - 1) * 6];
        int t = 0;
        for (int x = 0; x < res - 1; x++)
        {
            for (int y = 0; y < res - 1; y++)
            {
                int v00 = x * res + y;
                int v01 = x * res + (y + 1);
                int v10 = (x + 1) * res + y;
                int v11 = (x + 1) * res + (y + 1);
                indices[t++] = v00;
                indices[t++] = v10;
                indices[t++] = v11;
                indices[t++] = v00;
                indices[t++] = v11;
                indices[t++] = v01;
            }
        }

        // Smooth normals: accumulate each triangle's (area-weighted) face normal into its three vertices,
        // then normalize. Done with raw float accumulators rather than Godot.Vector3 operators — the
        // profiler flagged the per-triangle Vector3 add/sub/cross/normalize (~11% of terrain build) as
        // operator/ctor overhead. The arithmetic is identical (Vector3.Cross expanded inline), so the
        // resulting normals — and the shading — are unchanged.
        var nx = new float[vertexCount];
        var ny = new float[vertexCount];
        var nz = new float[vertexCount];
        for (int i = 0; i < indices.Length; i += 3)
        {
            int a = indices[i];
            int b = indices[i + 1];
            int c = indices[i + 2];
            Vector3 pa = positions[a];
            float e1x = positions[b].X - pa.X;
            float e1y = positions[b].Y - pa.Y;
            float e1z = positions[b].Z - pa.Z;
            float e2x = positions[c].X - pa.X;
            float e2y = positions[c].Y - pa.Y;
            float e2z = positions[c].Z - pa.Z;
            // Cross(e2, e1): the triangles wind so that Cross(e1, e2) faces down, which left the terrain
            // lit only by ambient (no sun term, so no shadows and no slope shading).
            float fnx = e1z * e2y - e1y * e2z;
            float fny = e1x * e2z - e1z * e2x;
            float fnz = e1y * e2x - e1x * e2y;
            nx[a] += fnx;
            ny[a] += fny;
            nz[a] += fnz;
            nx[b] += fnx;
            ny[b] += fny;
            nz[b] += fnz;
            nx[c] += fnx;
            ny[c] += fny;
            nz[c] += fnz;
        }
        var normals = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            float len = Mathf.Sqrt(nx[i] * nx[i] + ny[i] * ny[i] + nz[i] * nz[i]);
            if (len > 0f)
                normals[i] = new Vector3(nx[i] / len, ny[i] / len, nz[i] / len);
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = positions;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, layerTextures != null && splat != null
            ? BuildSplatMaterial(layerTextures, splat)
            : SharedMaterial);

        return new MeshInstance3D
        {
            Mesh = mesh,
            Name = $"Tile_{tile.CoordX}_{tile.CoordY}",
        };
    }

    // A per-tile ShaderMaterial: the shared 8 layer textures plus this tile's two splat control textures
    // (the 8 weights packed into two RGBA8 images, sampled by UV2).
    private static ShaderMaterial BuildSplatMaterial(ImageTexture[] layers, SplatmapTile splat)
    {
        var material = new ShaderMaterial { Shader = SplatShader };
        for (int i = 0; i < layers.Length; i++)
            material.SetShaderParameter($"layer{i}", layers[i]);
        material.SetShaderParameter("control0", ControlTexture(splat, 0));
        material.SetShaderParameter("control1", ControlTexture(splat, 4));
        return material;
    }

    // Packs 4 splat layers (firstLayer..firstLayer+3) into an RGBA8 image. Image pixel (x, y) — addressed
    // (y * res + x) — carries the weights the splatmap stores at [x, y], so UV2 (which runs x->u, y->v)
    // samples the same texel TerrainColor blends per vertex.
    private static ImageTexture ControlTexture(SplatmapTile splat, int firstLayer)
    {
        const int res = Landscape.SPLATMAP_RESOLUTION;
        float[] weights = splat.Weights;
        var bytes = new byte[res * res * 4];
        for (int x = 0; x < res; x++)
        {
            for (int y = 0; y < res; y++)
            {
                int src = SplatmapTile.WeightIndex(x, y, firstLayer);
                int dst = (y * res + x) * 4;
                bytes[dst + 0] = (byte)(weights[src + 0] * 255f);
                bytes[dst + 1] = (byte)(weights[src + 1] * 255f);
                bytes[dst + 2] = (byte)(weights[src + 2] * 255f);
                bytes[dst + 3] = (byte)(weights[src + 3] * 255f);
            }
        }
        return ImageTexture.CreateFromImage(Image.CreateFromData(res, res, false, Image.Format.Rgba8, bytes));
    }
}
