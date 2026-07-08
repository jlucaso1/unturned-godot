using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// Builds the map's roads: Bezier splines from Paths.dat lofted into ribbons at the widths from Roads.dat,
// textured with the real asphalt/dirt textures from the map's Roads.unity3d (a legacy UnityRaw bundle).
// One material is shared per road-material index (highway, dirt, ...) so the many roads batch. If the
// bundle can't be read, roads fall back to a procedural asphalt/dirt shader.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class RoadsBuilder
{
    private const float SampleSpacing = 3f;   // metres between spline samples
    private const float FallbackRepeat = 24f; // metres per texture tile when drawing procedurally
    // Uniform z-fight offset: with the road conformed to the mesh's own triangulation it is coplanar with the
    // terrain, so it needs a hair of lift. The same value for every road keeps junctions flush.
    private const float SurfaceLift = 0.05f;

    private static readonly Shader RoadShader = new()
    {
        Code = """
        shader_type spatial;
        render_mode cull_disabled;
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
        var byMaterial = new Dictionary<int, (Material material, float repeat)>();
        // Roads of the same material index share texture, tiling and width, so merge their ribbons into one
        // mesh per material — a handful of draw calls instead of one per road.
        var merged = new Dictionary<int, (SurfaceTool tool, Material material, int verts)>();

        int built = 0, textured = 0;
        for (int i = 0; i < roads.Count; i++)
        {
            int mat = roads[i].Material;
            RoadMaterialConfig config = mat < configs.Count
                ? configs[mat]
                : new RoadMaterialConfig(8f, 4f, 0.2f, 0f, true);

            if (!byMaterial.TryGetValue(mat, out (Material material, float repeat) shared))
            {
                shared = SharedMaterial(mat, config, textures, pavedFallback, dirtFallback);
                byMaterial[mat] = shared;
                if (shared.material is StandardMaterial3D)
                    textured++;
            }

            if (!merged.TryGetValue(mat, out (SurfaceTool tool, Material material, int verts) acc))
            {
                var st = new SurfaceTool();
                st.Begin(Mesh.PrimitiveType.Triangles);
                acc = (st, shared.material, 0);
                merged[mat] = acc;
            }
            // Unturned's RoadMaterial.HalfWidth returns the width field as-is (it is the half-width), so
            // the full road is 2 * config.Width.
            int added = AppendRoad(acc.tool, acc.verts, roads[i], shared.repeat, config.Width, heights);
            if (added > 0)
            {
                merged[mat] = (acc.tool, acc.material, acc.verts + added);
                built++;
            }
        }

        int meshCount = 0;
        foreach (KeyValuePair<int, (SurfaceTool tool, Material material, int verts)> kv in merged)
            if (kv.Value.verts > 0)
            {
                root.AddChild(CommitRoad(kv.Value.tool, kv.Value.material, $"Roads_{kv.Key}"));
                meshCount++;
            }
        GD.Print($"[unturned-godot] Roads: {built}/{roads.Count} built into {meshCount} meshes, " +
            $"{textured} textured materials");
        return root;
    }

    // One material per road-material index: the real Roads.unity3d texture (tiled by the config height),
    // or the procedural asphalt/dirt shader when no texture is available.
    private static (Material, float) SharedMaterial(int mat, RoadMaterialConfig config,
        List<ImageTexture?> textures, ShaderMaterial paved, ShaderMaterial dirt)
    {
        ImageTexture? texture = mat < textures.Count ? textures[mat] : null;
        if (texture == null)
            return (config.IsConcrete ? paved : dirt, FallbackRepeat);

        var material = new StandardMaterial3D
        {
            AlbedoTexture = texture,
            Roughness = 1f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, // seen from above; winding varies with curves
        };
        // Unturned tiles the texture every texHeight/config.Height metres of road length.
        float repeat = config.Height > 0f ? texture.GetHeight() / config.Height : texture.GetHeight();
        return (material, repeat);
    }

    // Appends one road as a flat ribbon whose centreline conforms to the terrain (ConformedPoint samples the
    // mesh's own triangulation, so the road lands exactly on the surface) with a single hair-thin, uniform
    // z-lift. Because the lift is the same for every road and the base matches the mesh, roads meet the ground
    // without a lip and meet each other at junctions without a step — where per-material bed heights (Depth)
    // would otherwise leave a gap. Returns the vertex count added.
    private static int AppendRoad(SurfaceTool st, int baseVertex, PlacedRoad road, float repeat, float halfWidth,
        HeightmapSampler heights)
    {
        if (road.Joints.Count < 2)
            return 0;

        var points = new List<Vector3>();
        var distances = new List<float>();
        float distance = 0f;
        Vector3 previous = ConformedPoint(road.Joints[0], road.Joints[0], 0f, heights);
        int segments = road.IsLoop ? road.Joints.Count : road.Joints.Count - 1;
        for (int s = 0; s < segments; s++)
        {
            RoadJoint a = road.Joints[s];
            RoadJoint b = road.Joints[(s + 1) % road.Joints.Count];
            float segLength = (b.Vertex - a.Vertex).Length();
            int steps = Mathf.Max(2, Mathf.CeilToInt(segLength / SampleSpacing));
            for (int step = s == 0 ? 0 : 1; step <= steps; step++)
            {
                Vector3 p = ConformedPoint(a, b, step / (float)steps, heights);
                distance += (p - previous).Length();
                points.Add(p);
                distances.Add(distance);
                previous = p;
            }
        }
        if (points.Count < 2)
            return 0;

        for (int i = 0; i < points.Count; i++)
        {
            Vector3 forward = Direction(points, i, road.IsLoop);
            Vector3 side = forward.Cross(Vector3.Up).Normalized();
            float v = distances[i] / repeat;
            AddVertex(st, points[i] + (side * halfWidth), 0f, v);
            AddVertex(st, points[i] - (side * halfWidth), 1f, v);
        }
        for (int i = 0; i + 1 < points.Count; i++)
        {
            int l = baseVertex + (i * 2);
            int r = l + 1;
            int ln = baseVertex + ((i + 1) * 2);
            int rn = ln + 1;
            st.AddIndex(l); st.AddIndex(r); st.AddIndex(ln);
            st.AddIndex(r); st.AddIndex(rn); st.AddIndex(ln);
        }

        return points.Count * 2;
    }

    private static void AddVertex(SurfaceTool st, Vector3 position, float u, float v)
    {
        st.SetNormal(Vector3.Up);
        st.SetUV(new Vector2(u, v));
        st.AddVertex(position);
    }

    // One centreline point (Godot space) at t along the a->b segment: its XZ from the Bezier, its height
    // conformed to the terrain (unless the start joint ignores terrain, e.g. bridges), plus the interpolated
    // per-joint offset and a uniform z-lift just large enough to avoid z-fighting with the terrain mesh.
    private static Vector3 ConformedPoint(RoadJoint a, RoadJoint b, float t, HeightmapSampler heights)
    {
        Vector3 p = Landscape.UnityToGodot(LevelRoads.BezierPoint(a, b, t));
        if (!a.IgnoreTerrain && heights.TrySampleHeight(p.X, -p.Z, out float ground)) // Godot Z -> Unity Z
            p.Y = ground;
        p.Y += Mathf.Lerp(a.Offset, b.Offset, t) + SurfaceLift;
        return p;
    }

    private static MeshInstance3D CommitRoad(SurfaceTool st, Material material, string name) =>
        new()
        {
            Name = name,
            Mesh = st.Commit(),
            MaterialOverride = material,
            // Flat ribbons that hug the terrain: their shadow is invisible, so don't re-draw them into
            // every directional cascade.
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };

    private static Vector3 Direction(List<Vector3> points, int i, bool loop)
    {
        Vector3 next = i + 1 < points.Count ? points[i + 1] : (loop ? points[0] : points[i]);
        Vector3 prev = i > 0 ? points[i - 1] : (loop ? points[^1] : points[i]);
        Vector3 dir = next - prev;
        return dir.LengthSquared() > 0f ? dir.Normalized() : Vector3.Forward;
    }

    // Decodes the road textures from Roads.unity3d in AssetBundle container order, which is the material
    // index order Unturned uses (material 0 -> Highway_0, 5 -> Trail, ...). The bundle is a small (~2 MB)
    // uncompressed UnityRaw file, so it's read each build rather than cached like the masterbundle.
    private static List<ImageTexture?> LoadRoadTextures(string environmentDir)
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
        return ModelLibrary.BuildTexture(new CachedTexture(tex.Format, tex.Width, tex.Height, tex.MipCount, pixels));
    }
}
