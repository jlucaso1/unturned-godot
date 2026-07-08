using Godot;
using UnturnedGodot.Data;

namespace UnturnedGodot;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class TerrainBuilder
{
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

        // Winding matches the old non-indexed build exactly (kept double-sided below), so shading is
        // unchanged apart from becoming smooth.
        for (int x = 0; x < res - 1; x++)
        {
            for (int y = 0; y < res - 1; y++)
            {
                int v00 = x * res + y;
                int v01 = x * res + (y + 1);
                int v10 = (x + 1) * res + y;
                int v11 = (x + 1) * res + (y + 1);

                st.AddIndex(v00);
                st.AddIndex(v11);
                st.AddIndex(v10);

                st.AddIndex(v00);
                st.AddIndex(v01);
                st.AddIndex(v11);
            }
        }

        st.GenerateNormals();

        var material = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, // avoid winding-order guesswork for the PoC
            Roughness = 1.0f,
        };
        st.SetMaterial(material);

        return new MeshInstance3D
        {
            Mesh = st.Commit(),
            Name = $"Tile_{tile.CoordX}_{tile.CoordY}",
        };
    }
}
