using System;
using System.IO;

namespace UnturnedGodot.Data;

// Builds the height/index grid for a coarse terrain occluder without depending on RenderingServer.
// Every coarse vertex uses the minimum of the (up to four) coarse cells touching it. Consequently all
// three vertices of an occluder triangle are no higher than the minimum source height anywhere in that
// cell; their interpolation therefore cannot rise through the rendered heightfield.
public sealed record ConservativeOccluderGrid(float[] Heights, int[] Indices, int VerticesPerEdge);

public static class ConservativeOccluder
{
    public static ConservativeOccluderGrid Build(int resolutionMinusOne, int cells,
        Func<int, int, float> heightAt)
    {
        ArgumentNullException.ThrowIfNull(heightAt);
        if (resolutionMinusOne <= 0)
            throw new ArgumentOutOfRangeException(nameof(resolutionMinusOne));
        if (cells <= 0 || cells > resolutionMinusOne || resolutionMinusOne % cells != 0)
            throw new ArgumentOutOfRangeException(nameof(cells));

        int step = resolutionMinusOne / cells;
        var cellMin = new float[cells * cells];
        for (int cx = 0; cx < cells; cx++)
            for (int cy = 0; cy < cells; cy++)
            {
                float lowest = float.MaxValue;
                int x0 = cx * step, y0 = cy * step;
                for (int x = x0; x <= x0 + step; x++)
                    for (int y = y0; y <= y0 + step; y++)
                    {
                        float height = heightAt(x, y);
                        if (!float.IsFinite(height))
                            throw new InvalidDataException("Terrain occluder source height is not finite.");
                        lowest = MathF.Min(lowest, height);
                    }
                cellMin[(cx * cells) + cy] = lowest;
            }

        int verts = cells + 1;
        var heights = new float[verts * verts];
        for (int gx = 0; gx < verts; gx++)
            for (int gy = 0; gy < verts; gy++)
            {
                float lowest = float.MaxValue;
                int firstX = Math.Max(0, gx - 1), lastX = Math.Min(cells - 1, gx);
                int firstY = Math.Max(0, gy - 1), lastY = Math.Min(cells - 1, gy);
                for (int cx = firstX; cx <= lastX; cx++)
                    for (int cy = firstY; cy <= lastY; cy++)
                        lowest = MathF.Min(lowest, cellMin[(cx * cells) + cy]);
                heights[(gx * verts) + gy] = lowest;
            }

        var indices = new int[cells * cells * 6];
        int at = 0;
        for (int gx = 0; gx < cells; gx++)
            for (int gy = 0; gy < cells; gy++)
            {
                int a = (gx * verts) + gy;
                int b = a + 1;
                int c = a + verts;
                int d = c + 1;
                indices[at++] = a; indices[at++] = c; indices[at++] = b;
                indices[at++] = b; indices[at++] = c; indices[at++] = d;
            }
        return new ConservativeOccluderGrid(heights, indices, verts);
    }
}
