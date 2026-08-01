using Godot;
using UnturnedGodot.Data;

namespace UnturnedGodot;

// A coarse, conservative occluder mesh per terrain tile, fed to Godot's CPU occlusion culling.
//
// The frustum alone cannot reject anything hidden BEHIND terrain: on a hilly map most of what is
// submitted every frame is on the far side of a ridge. Occlusion culling rasterises these occluders on
// the CPU each frame and drops whatever they cover, which costs no VRAM and — unlike merging geometry
// into chunks — leaves the instanced object rendering exactly as it is.
//
// Two properties matter for correctness:
//
//   Coarse. The occluder is rasterised every frame on the CPU, so it must be cheap. A tile's heightmap
//   is 257x257; sampling it on a 17x17 grid keeps the silhouette of a hill while costing ~512 triangles
//   per tile instead of ~131k.
//
//   Conservative. An occluder that pokes ABOVE the real ground would hide geometry that is actually
//   visible — the worst kind of bug, because it looks like objects popping out of existence. Each coarse
//   vertex therefore takes the MINIMUM height of the heightmap cells it spans, never the average, so the
//   occluder always sits at or below the surface it stands for.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class TerrainOccluder
{
    // Heightmap samples per occluder quad edge. 16 quads across a 1024 m tile is a 64 m grid: coarse
    // enough to rasterise cheaply, fine enough to keep a hill's outline.
    private const int Step = Landscape.HEIGHTMAP_RESOLUTION_MINUS_ONE / 16;

    public sealed record Prepared(Vector3[] Vertices, int[] Indices, string Name);

    // Pure data preparation: safe to run beside terrain mesh/LOD generation on worker threads.
    public static Prepared Prepare(in TerrainBuilder.TileMesh tile)
    {
        const int res = Landscape.HEIGHTMAP_RESOLUTION_MINUS_ONE;
        const int cells = res / Step;          // quads per edge
        TerrainBuilder.TileMesh source = tile; // an `in` parameter cannot be captured by the sampler
        ConservativeOccluderGrid grid = ConservativeOccluder.Build(res, cells,
            (x, y) => source.HeightAt((x * Landscape.HEIGHTMAP_RESOLUTION) + y));
        int verts = grid.VerticesPerEdge;

        var vertices = new Vector3[verts * verts];
        for (int gx = 0; gx < verts; gx++)
            for (int gy = 0; gy < verts; gy++)
            {
                int hx = System.Math.Min(gx * Step, res);
                int hy = System.Math.Min(gy * Step, res);
                vertices[(gx * verts) + gy] = Landscape.UnityToGodot(
                    Landscape.GetWorldPosition(tile.X, tile.Y, hx, hy, grid.Heights[(gx * verts) + gy]));
            }

        return new Prepared(vertices, grid.Indices, $"Occluder_{tile.X}_{tile.Y}");
    }

    // RenderingServer resource creation stays on the main thread.
    public static OccluderInstance3D Finish(Prepared prepared)
    {
        var occluder = new ArrayOccluder3D();
        occluder.SetArrays(prepared.Vertices, prepared.Indices);
        return new OccluderInstance3D
        {
            Name = prepared.Name,
            Occluder = occluder,
        };
    }
}
