using Godot;
using UnturnedGodot.Data;

namespace UnturnedGodot;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class TerrainBuilder
{
    // One material shared by every tile (#4): 16 identical StandardMaterial3D instances become one, so
    // the renderer batches state instead of switching per tile. Back-face culling (#3) halves fill rate
    // — the ground is only ever seen from above, so drawing its underside was wasted work.
    private static readonly StandardMaterial3D SharedMaterial = new()
    {
        VertexColorUseAsAlbedo = true,
        CullMode = BaseMaterial3D.CullModeEnum.Back,
        Roughness = 1.0f,
    };

    public static MeshInstance3D BuildTile(HeightmapTile tile, SplatmapTile? splat)
    {
        const int res = Landscape.HEIGHTMAP_RESOLUTION;
        int vertexCount = res * res;

        // Build the mesh arrays in C# and hand them to the engine in a single AddSurfaceFromArrays call,
        // instead of SurfaceTool's per-element AddVertex/SetColor/AddIndex + GenerateNormals. The profiler
        // showed that path was ~44% of world-build time (GenerateNormals alone 34%), almost all of it
        // marshaling ~400k managed→native calls per tile. Vertex (x, y) lands at index x * res + y.
        var positions = new Vector3[vertexCount];
        var colors = new Color[vertexCount];
        for (int x = 0; x < res; x++)
        {
            for (int y = 0; y < res; y++)
            {
                int idx = x * res + y;
                float h01 = tile.Heights[x, y];
                Vector3 unity = Landscape.GetWorldPosition(tile.CoordX, tile.CoordY, x, y, h01);
                positions[idx] = Landscape.UnityToGodot(unity);
                colors[idx] = TerrainColor.ForVertex(splat, x, y, unity.Y);
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
        arrays[(int)Mesh.ArrayType.Index] = indices;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, SharedMaterial);

        return new MeshInstance3D
        {
            Mesh = mesh,
            Name = $"Tile_{tile.CoordX}_{tile.CoordY}",
        };
    }
}
