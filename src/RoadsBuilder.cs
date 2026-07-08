using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Data;

namespace UnturnedGodot;

// Builds the map's roads: Bezier splines from Paths.dat lofted into ribbons at the widths from Roads.dat.
// These are the highways and dirt roads connecting the towns, which the terrain splatmap alone only hints
// at. The map's real road textures ship in a legacy UnityRaw bundle our reader can't open, so the asphalt
// (dark surface, yellow centre line, dashed lane lines) and dirt are drawn procedurally from the UVs.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class RoadsBuilder
{
    private const float SampleSpacing = 3f;   // metres between spline samples
    private const float HeightOffset = 0.3f;  // raise above terrain to avoid z-fighting

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

    public static Node3D Build(string environmentDir)
    {
        var root = new Node3D { Name = "Roads" };
        List<PlacedRoad> roads = LevelRoads.LoadPaths(Path.Combine(environmentDir, "Paths.dat"));
        if (roads.Count == 0)
            return root;

        List<RoadMaterialConfig> materials = LevelRoads.LoadMaterials(Path.Combine(environmentDir, "Roads.dat"));

        var paved = new ShaderMaterial { Shader = RoadShader };
        paved.SetShaderParameter("paved", true);
        var dirt = new ShaderMaterial { Shader = RoadShader };
        dirt.SetShaderParameter("paved", false);

        // Roads use exactly two materials (paved / dirt), so merge all their geometry into one mesh per
        // material — two draw calls total instead of one per road.
        var pavedTool = new SurfaceTool();
        pavedTool.Begin(Mesh.PrimitiveType.Triangles);
        var dirtTool = new SurfaceTool();
        dirtTool.Begin(Mesh.PrimitiveType.Triangles);
        int pavedVerts = 0, dirtVerts = 0, built = 0;

        for (int i = 0; i < roads.Count; i++)
        {
            RoadMaterialConfig config = roads[i].Material < materials.Count
                ? materials[roads[i].Material]
                : new RoadMaterialConfig(8f, 4f, 0.2f, 0f, true);

            SurfaceTool tool = config.IsConcrete ? pavedTool : dirtTool;
            int baseVertex = config.IsConcrete ? pavedVerts : dirtVerts;
            int added = AppendRoad(tool, baseVertex, roads[i], config);
            if (added == 0)
                continue;
            if (config.IsConcrete)
                pavedVerts += added;
            else
                dirtVerts += added;
            built++;
        }

        if (pavedVerts > 0)
            root.AddChild(CommitRoads(pavedTool, paved, "RoadsPaved"));
        if (dirtVerts > 0)
            root.AddChild(CommitRoads(dirtTool, dirt, "RoadsDirt"));

        int meshes = (pavedVerts > 0 ? 1 : 0) + (dirtVerts > 0 ? 1 : 0);
        GD.Print($"[unturned-godot] Roads: {built}/{roads.Count} built into {meshes} meshes");
        return root;
    }

    // Appends one road's ribbon geometry to a shared SurfaceTool, offsetting its indices past the vertices
    // already added. Returns the number of vertices contributed (0 if the road is too short to loft).
    private static int AppendRoad(SurfaceTool st, int baseVertex, PlacedRoad road, RoadMaterialConfig config)
    {
        if (road.Joints.Count < 2)
            return 0;

        // Sample the whole spline into Godot-space centreline points with accumulated distance.
        var points = new List<Vector3>();
        var distances = new List<float>();
        float distance = 0f;
        Vector3 previous = Landscape.UnityToGodot(road.Joints[0].Vertex);
        int segments = road.IsLoop ? road.Joints.Count : road.Joints.Count - 1;
        for (int s = 0; s < segments; s++)
        {
            RoadJoint a = road.Joints[s];
            RoadJoint b = road.Joints[(s + 1) % road.Joints.Count];
            float segLength = (b.Vertex - a.Vertex).Length();
            int steps = Mathf.Max(2, Mathf.CeilToInt(segLength / SampleSpacing));
            for (int step = s == 0 ? 0 : 1; step <= steps; step++)
            {
                Vector3 p = Landscape.UnityToGodot(LevelRoads.BezierPoint(a, b, step / (float)steps));
                distance += (p - previous).Length();
                points.Add(p);
                distances.Add(distance);
                previous = p;
            }
        }
        if (points.Count < 2)
            return 0;

        float halfWidth = config.Width * 0.5f;
        const float repeat = 24f; // metres per UV-length unit, sets the dashed-line spacing

        for (int i = 0; i < points.Count; i++)
        {
            Vector3 forward = Direction(points, i, road.IsLoop);
            Vector3 side = forward.Cross(Vector3.Up).Normalized();
            Vector3 up = Vector3.Up * HeightOffset;
            float v = distances[i] / repeat;

            st.SetNormal(Vector3.Up);
            st.SetUV(new Vector2(0f, v));
            st.AddVertex(points[i] + (side * halfWidth) + up);
            st.SetNormal(Vector3.Up);
            st.SetUV(new Vector2(1f, v));
            st.AddVertex(points[i] - (side * halfWidth) + up);
        }
        for (int i = 0; i + 1 < points.Count; i++)
        {
            int l = baseVertex + (i * 2);
            int r = baseVertex + (i * 2) + 1;
            int ln = baseVertex + ((i + 1) * 2);
            int rn = baseVertex + ((i + 1) * 2) + 1;
            st.AddIndex(l); st.AddIndex(r); st.AddIndex(ln);
            st.AddIndex(r); st.AddIndex(rn); st.AddIndex(ln);
        }

        return points.Count * 2;
    }

    // Flat ribbons ~0.3 m above the terrain cast an essentially invisible shadow, yet are re-drawn into
    // every directional cascade — skip it. All roads of one material are already merged into st here.
    private static MeshInstance3D CommitRoads(SurfaceTool st, Material material, string name) =>
        new()
        {
            Name = name,
            Mesh = st.Commit(),
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };

    private static Vector3 Direction(List<Vector3> points, int i, bool loop)
    {
        Vector3 next = i + 1 < points.Count ? points[i + 1] : (loop ? points[0] : points[i]);
        Vector3 prev = i > 0 ? points[i - 1] : (loop ? points[^1] : points[i]);
        Vector3 dir = next - prev;
        return dir.LengthSquared() > 0f ? dir.Normalized() : Vector3.Forward;
    }
}
