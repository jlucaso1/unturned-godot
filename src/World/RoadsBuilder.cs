using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// Builds the map's roads: Bezier splines from Paths.dat lofted through RoadMesh — the 1:1 port of
// Road.buildMesh's banked crown-and-skirt cross-section — at the widths/depths from Roads.dat, textured
// with the real asphalt/dirt textures from the map's Roads.unity3d (a legacy UnityRaw bundle). One material
// is shared per road-material index (highway, dirt, ...) so the many roads batch. If the bundle can't be
// read, roads fall back to a procedural asphalt/dirt shader.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class RoadsBuilder
{
    private const float FallbackRepeat = 24f; // metres per texture tile when drawing procedurally

    private static readonly Shader RoadShader = new()
    {
        Code = """
        shader_type spatial;
        render_mode cull_back; // Standard/Diffuse (the road shader) serializes Cull Back
        uniform bool paved = true;
        void fragment() {
            vec3 asphalt = vec3(0.11, 0.11, 0.12);
            vec3 dirt = vec3(0.42, 0.31, 0.21);
            if (paved) {
                vec3 col = asphalt;
                if (abs(UV.x - 0.5) < 0.03) col = vec3(0.82, 0.68, 0.12);      // yellow centre line
                float dash = step(0.55, fract(UV.y * 6.0));
                if (abs(UV.x - 0.15) < 0.02 && dash > 0.5) col = vec3(0.8);    // white lane lines
                if (abs(UV.x - 0.85) < 0.02 && dash > 0.5) col = vec3(0.8);
                ALBEDO = col;
            } else {
                ALBEDO = dirt;
            }
            ROUGHNESS = 1.0;
        }
        """,
    };

    public static Node3D Build(string environmentDir, HeightmapSampler heights)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var root = new Node3D { Name = "Roads" };
        List<PlacedRoad> roads = LevelRoads.LoadPaths(Path.Combine(environmentDir, "Paths.dat"));
        if (roads.Count == 0)
            return root;

        List<RoadMaterialConfig> configs = LevelRoads.LoadMaterials(Path.Combine(environmentDir, "Roads.dat"));
        List<ImageTexture?> textures = LoadRoadTextures(environmentDir);

        var pavedFallback = new ShaderMaterial { Shader = RoadShader };
        pavedFallback.SetShaderParameter("paved", true);
        var dirtFallback = new ShaderMaterial { Shader = RoadShader };
        dirtFallback.SetShaderParameter("paved", false);
        var terrain = new SampledTerrain(heights);
        var byMaterial = new Dictionary<int, (Material material, float inverseRepeat)>();
        bool useArrays = OS.GetEnvironment("UG_ROAD_ARRAYS") != "0";
        // Roads of the same material index share texture, tiling and width, so merge their strips into one
        // mesh per material — a handful of draw calls instead of one per road.
        var merged = new Dictionary<int, (SurfaceTool tool, Material material, int verts)>();
        var arrayMeshes = new Dictionary<int, (List<RoadMeshData> meshes, Material material)>();

        int built = 0, textured = 0;
        for (int i = 0; i < roads.Count; i++)
        {
            int mat = roads[i].Material;
            RoadMaterialConfig config = mat < configs.Count
                ? configs[mat]
                : new RoadMaterialConfig(8f, 4f, 0.2f, 0f, true);

            if (!byMaterial.TryGetValue(mat, out (Material material, float inverseRepeat) shared))
            {
                shared = SharedMaterial(mat, config, textures, pavedFallback, dirtFallback);
                byMaterial[mat] = shared;
                if (shared.material is StandardMaterial3D)
                    textured++;
            }

            RoadMeshData roadMesh = RoadMesh.Build(roads[i], config, shared.inverseRepeat, terrain);
            if (roadMesh.Vertices.Length == 0)
                continue;

            if (useArrays)
            {
                if (!arrayMeshes.TryGetValue(mat, out (List<RoadMeshData> meshes, Material material) group))
                    arrayMeshes[mat] = group = (new List<RoadMeshData>(), shared.material);
                group.meshes.Add(roadMesh);
            }
            else
            {
                if (!merged.TryGetValue(mat, out (SurfaceTool tool, Material material, int verts) acc))
                {
                    var st = new SurfaceTool();
                    st.Begin(Mesh.PrimitiveType.Triangles);
                    acc = (st, shared.material, 0);
                }
                AppendRoad(acc.tool, acc.verts, roadMesh);
                merged[mat] = (acc.tool, acc.material, acc.verts + roadMesh.Vertices.Length);
            }
            built++;
        }

        int meshCount = 0;
        if (useArrays)
        {
            foreach (KeyValuePair<int, (List<RoadMeshData> meshes, Material material)> kv in arrayMeshes)
            {
                root.AddChild(CommitRoad(kv.Value.meshes, kv.Value.material, $"Roads_{kv.Key}"));
                meshCount++;
            }
        }
        else
            foreach (KeyValuePair<int, (SurfaceTool tool, Material material, int verts)> kv in merged)
                if (kv.Value.verts > 0)
                {
                    root.AddChild(CommitRoad(kv.Value.tool, kv.Value.material, $"Roads_{kv.Key}"));
                    meshCount++;
                }
        Log.Print($"[unturned-godot] Roads: {built}/{roads.Count} built into {meshCount} meshes, " +
            $"{textured} textured materials in {watch.ElapsedMilliseconds} ms "
            + $"({(useArrays ? "direct arrays" : "SurfaceTool")})");
        return root;
    }

    // One material per road-material index: the real Roads.unity3d texture (tiled by the config height),
    // or the procedural asphalt/dirt shader when no texture is available. The float is the source's
    // inverseTextureRepeatDistance: v advances this much per metre of road.
    private static (Material, float) SharedMaterial(int mat, RoadMaterialConfig config,
        List<ImageTexture?> textures, ShaderMaterial paved, ShaderMaterial dirt)
    {
        ImageTexture? texture = mat < textures.Count ? textures[mat] : null;
        if (texture == null)
            return (config.IsConcrete ? paved : dirt, 1f / FallbackRepeat);

        var material = new StandardMaterial3D
        {
            AlbedoTexture = texture,
            Roughness = 1f,
            // Standard/Diffuse — the shader RoadMaterial instantiates — serializes Cull Back in the
            // game data, and the RoadMesh port keeps the source winding, so back-face culling is exact.
            CullMode = BaseMaterial3D.CullModeEnum.Back,
            // Roads are viewed at grazing angles; plain trilinear collapses to blurry mips ~10 m out.
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
        // Unturned tiles the texture every texHeight/config.Height metres of road length.
        float repeat = config.Height > 0f ? texture.GetHeight() / config.Height : texture.GetHeight();
        return (material, 1f / repeat);
    }

    // Appends one road's RoadMesh strip (built in Unity space, faithful to Road.buildMesh) reflected into
    // Godot space: positions and normals negate Z, winding kept — the same F-translation every Unity-sourced
    // mesh here uses (UnityMeshConverter). Returns the vertex count added.
    private static void AppendRoad(SurfaceTool st, int baseVertex, RoadMeshData mesh)
    {
        for (int i = 0; i < mesh.Vertices.Length; i++)
        {
            Vector3 n = mesh.Normals[i];
            st.SetNormal(new Vector3(n.X, n.Y, -n.Z));
            st.SetUV(mesh.Uvs[i]);
            st.AddVertex(Landscape.UnityToGodot(mesh.Vertices[i]));
        }
        foreach (int index in mesh.Indices)
            st.AddIndex(baseVertex + index);
    }

    // RoadMesh's terrain queries (Unity space), answered from the heightmap sampler — the same triangulation
    // the terrain mesh renders with, so the road bed hugs the visible ground exactly. Off-map points keep
    // their own height on flat ground, like the source's out-of-bounds fallbacks.
    private sealed class SampledTerrain : IRoadTerrain
    {
        private readonly HeightmapSampler _heights;

        public SampledTerrain(HeightmapSampler heights) => _heights = heights;

        public float GetHeight(Vector3 position)
            => _heights.TrySampleHeight(position.X, position.Z, out float y) ? y : position.Y;

        public Vector3 GetNormal(Vector3 position)
            => _heights.TrySampleNormal(position.X, position.Z, out Vector3 normal) ? normal : Vector3.Up;
    }

    private static MeshInstance3D CommitRoad(SurfaceTool st, Material material, string name) =>
        new()
        {
            Name = name,
            Mesh = st.Commit(),
            MaterialOverride = material,
            // Low strips that hug the terrain: their shadow is invisible, so don't re-draw them into
            // every directional cascade.
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };

    private static MeshInstance3D CommitRoad(IReadOnlyList<RoadMeshData> meshes, Material material,
        string name)
    {
        int vertexCount = 0, indexCount = 0;
        foreach (RoadMeshData mesh in meshes)
        {
            vertexCount = checked(vertexCount + mesh.Vertices.Length);
            indexCount = checked(indexCount + mesh.Indices.Length);
        }
        var vertices = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var uvs = new Vector2[vertexCount];
        var indices = new int[indexCount];
        int vertexAt = 0, indexAt = 0;
        foreach (RoadMeshData mesh in meshes)
        {
            for (int i = 0; i < mesh.Vertices.Length; i++)
            {
                vertices[vertexAt + i] = Landscape.UnityToGodot(mesh.Vertices[i]);
                Vector3 normal = mesh.Normals[i];
                normals[vertexAt + i] = new Vector3(normal.X, normal.Y, -normal.Z);
                uvs[vertexAt + i] = mesh.Uvs[i];
            }
            for (int i = 0; i < mesh.Indices.Length; i++)
                indices[indexAt + i] = vertexAt + mesh.Indices[i];
            vertexAt += mesh.Vertices.Length;
            indexAt += mesh.Indices.Length;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var meshResource = new ArrayMesh();
        meshResource.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return new MeshInstance3D
        {
            Name = name,
            Mesh = meshResource,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    // Decodes the road textures from Roads.unity3d in AssetBundle container order, which is the material
    // index order Unturned uses (material 0 -> Highway_0, 5 -> Trail, ...). The bundle is a small (~2 MB)
    // uncompressed UnityRaw file, so it's read each build rather than cached like the masterbundle.
    private static List<ImageTexture?> LoadRoadTextures(string environmentDir)
    {
        try
        {
            return ReadRoadTextures(environmentDir);
        }
        catch (Exception e)
        {
            // Older maps (Alpha Valley, Monolith, ...) ship a pre-2018 bundle this reader does not
            // decode. Roads still build, textured by the procedural fallback shader.
            Log.Print($"[unturned-godot] Road textures unavailable ({e.GetType().Name}: {e.Message}); "
                + "falling back to the procedural road material.");
            return new List<ImageTexture?>();
        }
    }

    private static List<ImageTexture?> ReadRoadTextures(string environmentDir)
    {
        var result = new List<ImageTexture?>();
        string bundlePath = Path.Combine(environmentDir, "Roads.unity3d");
        if (!File.Exists(bundlePath))
            return result;

        byte[] data = File.ReadAllBytes(bundlePath);
        if (!UnityRawBundle.IsRaw(data))
            return result;

        UnityRawBundle raw = UnityRawBundle.Read(data);
        byte[]? sfBytes = null;
        foreach (KeyValuePair<string, byte[]> f in raw.Files)
            sfBytes = f.Value; // one CAB entry: the SerializedFile
        if (sfBytes == null)
            return result;

        SerializedFile file = SerializedFile.Read(sfBytes);
        var byId = new Dictionary<long, SerializedObject>();
        foreach (SerializedObject o in file.Objects)
            byId[o.PathId] = o;

        foreach (SerializedObject o in file.Objects)
        {
            if (o.ClassId != 142) // AssetBundle
                continue;
            Dictionary<string, object> ab = TypeTreeReader.Read(o.TypeTree, file.ReaderFor(o));
            foreach (object entry in (List<object>)ab["m_Container"])
            {
                var pair = (Dictionary<string, object>)entry;
                var info = (Dictionary<string, object>)pair["second"];
                long assetId = Convert.ToInt64(((Dictionary<string, object>)info["asset"])["m_PathID"]);
                if (byId.TryGetValue(assetId, out SerializedObject? texObj) && texObj.ClassId == 28) // Texture2D
                    result.Add(DecodeTexture(texObj, file));
            }
        }
        return result;
    }

    private static ImageTexture? DecodeTexture(SerializedObject texObj, SerializedFile file)
    {
        UnityTexture tex = UnityTexture.Read(TypeTreeReader.Read(texObj.TypeTree, file.ReaderFor(texObj)));
        byte[]? pixels = tex.GetPixels(_ => null); // inline data (UnityRaw textures have no .resS stream)
        if (pixels == null || pixels.Length == 0)
            return null;
        return ModelLibrary.BuildTexture(CachedTexture.From(tex, pixels));
    }
}
