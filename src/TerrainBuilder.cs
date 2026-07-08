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

        var positions = new Vector3[res, res];
        var colors = new Color[res, res];
        for (int x = 0; x < res; x++)
        {
            for (int y = 0; y < res; y++)
            {
                float h01 = tile.Heights[x, y];
                Vector3 unity = Landscape.GetWorldPosition(tile.CoordX, tile.CoordY, x, y, h01);
                positions[x, y] = Landscape.UnityToGodot(unity);
                colors[x, y] = TerrainColor.ForVertex(splat, x, y, unity.Y);
            }
        }

        for (int x = 0; x < res - 1; x++)
        {
            for (int y = 0; y < res - 1; y++)
            {
                AddVertex(st, positions[x, y], colors[x, y]);
                AddVertex(st, positions[x + 1, y + 1], colors[x + 1, y + 1]);
                AddVertex(st, positions[x + 1, y], colors[x + 1, y]);

                AddVertex(st, positions[x, y], colors[x, y]);
                AddVertex(st, positions[x, y + 1], colors[x, y + 1]);
                AddVertex(st, positions[x + 1, y + 1], colors[x + 1, y + 1]);
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

    private static void AddVertex(SurfaceTool st, Vector3 pos, Color color)
    {
        st.SetColor(color);
        st.AddVertex(pos);
    }
}
