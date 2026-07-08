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

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        // One shared vertex per grid point, addressed by index — vs the old scheme that emitted all six
        // corners of every quad (~6x more vertices). GenerateNormals then averages the faces meeting at
        // each shared vertex, giving smooth terrain shading instead of a faceted look. Vertices are added
        // row-major so vertex (x, y) lands at index x * res + y.
        for (int x = 0; x < res; x++)
        {
            for (int y = 0; y < res; y++)
            {
                float h01 = tile.Heights[x, y];
                Vector3 unity = Landscape.GetWorldPosition(tile.CoordX, tile.CoordY, x, y, h01);
                st.SetColor(TerrainColor.ForVertex(splat, x, y, unity.Y));
                st.AddVertex(Landscape.UnityToGodot(unity));
            }
        }

        // Front faces point up (verified on screen): this makes normals face the sky for correct
        // lighting and lets back-face culling (#3) drop the underside. The old non-indexed build used the
        // reverse winding but hid it behind double-sided rendering, so its normals actually pointed down.
        for (int x = 0; x < res - 1; x++)
        {
            for (int y = 0; y < res - 1; y++)
            {
                int v00 = x * res + y;
                int v01 = x * res + (y + 1);
                int v10 = (x + 1) * res + y;
                int v11 = (x + 1) * res + (y + 1);

                st.AddIndex(v00);
                st.AddIndex(v10);
                st.AddIndex(v11);

                st.AddIndex(v00);
                st.AddIndex(v11);
                st.AddIndex(v01);
            }
        }

        st.GenerateNormals();
        st.SetMaterial(SharedMaterial);

        return new MeshInstance3D
        {
            Mesh = st.Commit(),
            Name = $"Tile_{tile.CoordX}_{tile.CoordY}",
        };
    }
}
