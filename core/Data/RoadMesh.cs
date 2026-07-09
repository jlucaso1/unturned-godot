using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Data;

// The terrain queries Road.buildMesh makes (LevelGround.getHeight / getNormal), in Unity space. The builder
// adapts HeightmapSampler; tests supply synthetic terrain.
public interface IRoadTerrain
{
    float GetHeight(Vector3 position);   // terrain Y under the point (or the point's own Y off the map)
    Vector3 GetNormal(Vector3 position); // upward surface normal at the point
}

// The finished road geometry, still in Unity space (the builder reflects it into Godot space).
public readonly struct RoadMeshData
{
    public readonly Vector3[] Vertices;
    public readonly Vector3[] Normals; // the terrain normal at each sample — roads shade like the ground
    public readonly Vector2[] Uvs;
    public readonly int[] Indices;     // Unity clockwise-front triangles

    public RoadMeshData(Vector3[] vertices, Vector3[] normals, Vector2[] uvs, int[] indices)
    {
        Vertices = vertices;
        Normals = normals;
        Uvs = uvs;
        Indices = indices;
    }
}

// A 1:1 port of the geometry half of Unturned's Road.buildMesh (Road.cs): the spline is sampled every 5 m,
// and each sample emits a 4-vertex cross-section — a flat crown between ±halfWidth raised halfVerticalSize
// above the terrain, with skirts falling to halfVerticalSize BELOW it at ±(halfWidth + verticalSize) — banked
// to the terrain via side = cross(direction, normal). Non-loop roads get start/end caps pulled out along the
// spline at skirt height, sealing the open profile against the ground. All vertex normals are the terrain
// normal, so the road lights exactly like the ground it lies on. Legacy material fields map as the source's
// RoadMaterial properties do: halfWidth = width, halfVerticalSize = depth, verticalOffset = offset.
public static class RoadMesh
{
    public const float SampleInterval = 5f; // metres between spline samples (Road.updateSamples)

    public static RoadMeshData Build(PlacedRoad road, RoadMaterialConfig config,
        float inverseTextureRepeatDistance, IRoadTerrain terrain)
    {
        if (road.Joints.Count < 2)
            return new RoadMeshData(Array.Empty<Vector3>(), Array.Empty<Vector3>(), Array.Empty<Vector2>(),
                Array.Empty<int>());

        float halfWidth = config.Width;        // RoadMaterial.HalfWidth ("misleadingly named" width field)
        float halfVerticalSize = config.Depth; // RoadMaterial.HalfVerticalSize
        float verticalSize = halfVerticalSize * 2f;
        float verticalOffset = config.Offset;  // RoadMaterial.VerticalOffset, along the terrain normal

        List<(int Index, float Time)> samples = Sample(road);

        // Non-loop roads reserve one 4-vertex row before the samples (start cap) and one after (end cap).
        int capOffset = road.IsLoop ? 0 : 4;
        int rows = samples.Count + (road.IsLoop ? 0 : 2);
        var vertices = new Vector3[rows * 4];
        var normals = new Vector3[rows * 4];
        var uvs = new Vector2[rows * 4];

        float distance = 0f;
        Vector3 prevPosition = Vector3.Zero;
        Vector3 position = Vector3.Zero, direction = Vector3.Zero, normal = Vector3.Up, side = Vector3.Zero;

        for (int index = 0; index < samples.Count; index++)
        {
            (int jointIndex, float time) = samples[index];
            RoadJoint joint = road.Joints[jointIndex];

            position = GetPosition(road, jointIndex, time);
            if (!joint.IgnoreTerrain)
                position.Y = terrain.GetHeight(position);

            direction = NormalizedOrZero(GetVelocity(road, jointIndex, time));
            normal = joint.IgnoreTerrain ? Vector3.Up : terrain.GetNormal(position);
            side = NormalizedOrZero(direction.Cross(normal));

            // Edge-height correction: if either crown edge would sit under the terrain (crossing a side
            // slope), lift the whole section by that burial — sequentially, left then right, as the source
            // does (the right check sees the already-lifted Y).
            if (!joint.IgnoreTerrain)
            {
                Vector3 leftSide = position + (side * halfWidth);
                float leftOffset = terrain.GetHeight(leftSide) - leftSide.Y;
                if (leftOffset > 0)
                    position.Y += leftOffset;

                Vector3 rightSide = position - (side * halfWidth);
                float rightOffset = terrain.GetHeight(rightSide) - rightSide.Y;
                if (rightOffset > 0)
                    position.Y += rightOffset;
            }

            // Per-joint vertical offset, interpolated to the next joint. Only a loop's closing segment
            // reaches the last joint index (non-loop samples stop at Count-2, so the source's third branch
            // there is dead code); its wrap back to joint 0 is the same wrap SegmentJoints does.
            int nextJoint = jointIndex + 1 == road.Joints.Count ? 0 : jointIndex + 1;
            position.Y += Mathf.Lerp(joint.Offset, road.Joints[nextJoint].Offset, time);

            // The cross-section: outer skirt, crown edge, crown edge, outer skirt (source vertex order).
            Vector3 lift = normal * verticalOffset;
            int row = capOffset + (index * 4);
            vertices[row] = position + (side * (halfWidth + verticalSize)) - (normal * halfVerticalSize) + lift;
            vertices[row + 1] = position + (side * halfWidth) + (normal * halfVerticalSize) + lift;
            vertices[row + 2] = position - (side * halfWidth) + (normal * halfVerticalSize) + lift;
            vertices[row + 3] = position - (side * (halfWidth + verticalSize)) - (normal * halfVerticalSize) + lift;
            normals[row] = normal;
            normals[row + 1] = normal;
            normals[row + 2] = normal;
            normals[row + 3] = normal;

            if (index == 0)
            {
                if (!road.IsLoop)
                {
                    // Start cap: the section flattened to skirt height, pulled back along the spline.
                    Vector3 pullBack = direction * verticalSize * 2f;
                    Vector3 skirt = lift - (normal * halfVerticalSize);
                    vertices[0] = position + (side * (halfWidth + verticalSize)) + skirt - pullBack;
                    vertices[1] = position + (side * halfWidth) + skirt - pullBack;
                    vertices[2] = position - (side * halfWidth) + skirt - pullBack;
                    vertices[3] = position - (side * (halfWidth + verticalSize)) + skirt - pullBack;
                    normals[0] = normal;
                    normals[1] = normal;
                    normals[2] = normal;
                    normals[3] = normal;
                    uvs[0] = Vector2.Zero;
                    uvs[1] = Vector2.Zero;
                    uvs[2] = Vector2.Right;
                    uvs[3] = Vector2.Right;
                }

                prevPosition = position;
                uvs[row] = Vector2.Zero;
                uvs[row + 1] = Vector2.Zero;
                uvs[row + 2] = Vector2.Right;
                uvs[row + 3] = Vector2.Right;
            }
            else
            {
                distance += (position - prevPosition).Length();
                prevPosition = position;

                float v = distance * inverseTextureRepeatDistance;
                uvs[row] = new Vector2(0f, v);
                uvs[row + 1] = new Vector2(0f, v);
                uvs[row + 2] = new Vector2(1f, v);
                uvs[row + 3] = new Vector2(1f, v);
            }
        }

        if (!road.IsLoop)
        {
            // End cap: mirror of the start cap, pushed forward from the last sample's section.
            Vector3 pushOut = direction * verticalSize * 2f;
            Vector3 skirt = (normal * verticalOffset) - (normal * halfVerticalSize);
            int row = 4 + (samples.Count * 4);
            vertices[row] = position + (side * (halfWidth + verticalSize)) + skirt + pushOut;
            vertices[row + 1] = position + (side * halfWidth) + skirt + pushOut;
            vertices[row + 2] = position - (side * halfWidth) + skirt + pushOut;
            vertices[row + 3] = position - (side * (halfWidth + verticalSize)) + skirt + pushOut;
            normals[row] = normal;
            normals[row + 1] = normal;
            normals[row + 2] = normal;
            normals[row + 3] = normal;

            float v = distance * inverseTextureRepeatDistance;
            uvs[row] = new Vector2(0f, v);
            uvs[row + 1] = new Vector2(0f, v);
            uvs[row + 2] = new Vector2(1f, v);
            uvs[row + 3] = new Vector2(1f, v);
        }

        return new RoadMeshData(vertices, normals, uvs, Triangulate(rows));
    }

    // Road.updateSamples: a sample every 5 m of estimated length, the residual carried across segments so
    // spacing stays even through joints, plus one closing sample (t=1 of the last segment, or the loop's
    // start). Loops sample every segment including last->first.
    private static List<(int Index, float Time)> Sample(PlacedRoad road)
    {
        var samples = new List<(int Index, float Time)>();
        float offset = 0f;
        int segments = road.Joints.Count - 1 + (road.IsLoop ? 1 : 0);
        for (int index = 0; index < segments; index++)
        {
            float length = GetLengthEstimate(road, index);
            float step;
            for (step = offset; step < length; step += SampleInterval)
                samples.Add((index, step / length));
            offset = step - length;
        }

        samples.Add(road.IsLoop ? (0, 0f) : (road.Joints.Count - 2, 1f));
        return samples;
    }

    // The source builds the strip in 20-sample segment objects, but consecutive segments share their border
    // row, so the seam-free union is one uniform strip: every pair of adjacent 4-vertex rows is bridged by
    // 6 triangles (right skirt, crown, left skirt — 18 indices), Unity clockwise-front winding.
    private static int[] Triangulate(int rows)
    {
        var indices = new int[(rows - 1) * 18];
        for (int tri = 0; tri < rows - 1; tri++)
        {
            int b = tri * 4;
            int o = tri * 18;
            indices[o] = b + 5;
            indices[o + 1] = b + 1;
            indices[o + 2] = b + 4;
            indices[o + 3] = b;
            indices[o + 4] = b + 4;
            indices[o + 5] = b + 1;
            indices[o + 6] = b + 6;
            indices[o + 7] = b + 2;
            indices[o + 8] = b + 5;
            indices[o + 9] = b + 1;
            indices[o + 10] = b + 5;
            indices[o + 11] = b + 2;
            indices[o + 12] = b + 7;
            indices[o + 13] = b + 3;
            indices[o + 14] = b + 6;
            indices[o + 15] = b + 2;
            indices[o + 16] = b + 6;
            indices[o + 17] = b + 3;
        }
        return indices;
    }

    // Road.getPosition(index, t): the segment's cubic Bezier, except anti-parallel tangents (a spline tool
    // degeneracy) fall back to a straight lerp between the joints.
    public static Vector3 GetPosition(PlacedRoad road, int index, float time)
    {
        (RoadJoint start, RoadJoint end) = SegmentJoints(road, ref index);
        time = Mathf.Clamp(time, 0f, 1f);

        if (NormalizedOrZero(start.Tangent1).Dot(NormalizedOrZero(end.Tangent0)) < -0.999f)
            return start.Vertex.Lerp(end.Vertex, time);
        return LevelRoads.BezierPoint(start, end, time);
    }

    // Road.getVelocity(index, t): the Bezier derivative — deliberately WITHOUT the anti-parallel fallback,
    // exactly like the source (the direction just degenerates there too).
    public static Vector3 GetVelocity(PlacedRoad road, int index, float time)
    {
        (RoadJoint start, RoadJoint end) = SegmentJoints(road, ref index);
        time = Mathf.Clamp(time, 0f, 1f);

        Vector3 a = start.Vertex;
        Vector3 b = start.Vertex + start.Tangent1;
        Vector3 c = end.Vertex + end.Tangent0;
        Vector3 d = end.Vertex;
        float u = 1f - time;
        return (3f * u * u * (b - a)) + (6f * u * time * (c - b)) + (3f * time * time * (d - c));
    }

    // Road.getLengthEstimate(index) via BezierTool: the average of the chord and the control polygon.
    public static float GetLengthEstimate(PlacedRoad road, int index)
    {
        (RoadJoint start, RoadJoint end) = SegmentJoints(road, ref index);

        if (NormalizedOrZero(start.Tangent1).Dot(NormalizedOrZero(end.Tangent0)) < -0.999f)
            return (end.Vertex - start.Vertex).Length();

        Vector3 a = start.Vertex;
        Vector3 b = start.Vertex + start.Tangent1;
        Vector3 c = end.Vertex + end.Tangent0;
        Vector3 d = end.Vertex;
        return ((d - a).Length() + (d - c).Length() + (b - c).Length() + (b - a).Length()) / 2f;
    }

    private static (RoadJoint Start, RoadJoint End) SegmentJoints(PlacedRoad road, ref int index)
    {
        index = Math.Clamp(index, 0, road.Joints.Count - 1);
        RoadJoint start = road.Joints[index];
        RoadJoint end = road.Joints[index == road.Joints.Count - 1 ? 0 : index + 1];
        return (start, end);
    }

    // Unity's Vector3.normalized: zero for near-zero vectors (Godot's Normalized() would NaN there).
    private static Vector3 NormalizedOrZero(Vector3 v)
    {
        float length = v.Length();
        return length > 1e-5f ? v / length : Vector3.Zero;
    }
}
