using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests;

public class SurfaceVertexSliceTests
{
    private static SurfaceVertexSlice.Result Compacted(int[] indices, int vertexCount)
    {
        SurfaceVertexSlice.Result? slice = SurfaceVertexSlice.Compact(indices, vertexCount);
        Assert.True(slice.HasValue, "expected the surface to compact");
        return slice!.Value;
    }

    // The shape this exists for: a mesh split across two materials, whose two surfaces index disjoint
    // halves of one shared pool. Each surface should carry its own half, not the whole thing.
    [Fact]
    public void Compact_KeepsOnlyTheVerticesTheSurfaceReferences()
    {
        SurfaceVertexSlice.Result slice = Compacted(new[] { 4, 5, 6 }, vertexCount: 8);

        Assert.Equal(new[] { 4, 5, 6 }, slice.Kept);
        Assert.Equal(new[] { 0, 1, 2 }, slice.Indices);
    }

    // Kept is ascending in source order regardless of the order the triangles reach the vertices in.
    // Simplification resolves vertices sharing a position to the lowest-numbered one, so a first-use
    // ordering here could hand meshoptimizer a different representative and change the LOD chain.
    [Fact]
    public void Compact_OrdersKeptVerticesBySourceIndexNotByFirstUse()
    {
        SurfaceVertexSlice.Result slice = Compacted(new[] { 6, 2, 4 }, vertexCount: 8);

        Assert.Equal(new[] { 2, 4, 6 }, slice.Kept);
        Assert.Equal(new[] { 2, 0, 1 }, slice.Indices);
    }

    // The triangle a surface draws has to come out of the remap unchanged: same vertices, same winding.
    [Fact]
    public void Compact_PreservesEveryTriangleThroughTheRemap()
    {
        int[] indices = { 9, 3, 6, 6, 3, 1 };

        SurfaceVertexSlice.Result slice = Compacted(indices, vertexCount: 10);

        for (int i = 0; i < indices.Length; i++)
            Assert.Equal(indices[i], slice.Kept[slice.Indices[i]]);
    }

    // A repeated vertex is one vertex. Without this the "kept" pool would be as long as the index buffer.
    [Fact]
    public void Compact_CountsARepeatedVertexOnce()
    {
        SurfaceVertexSlice.Result slice = Compacted(new[] { 3, 3, 3, 5, 3 }, vertexCount: 6);

        Assert.Equal(new[] { 3, 5 }, slice.Kept);
        Assert.Equal(new[] { 0, 0, 0, 1, 0 }, slice.Indices);
    }

    // The single-surface case, which is most of any map: the surface already reaches the whole pool, so
    // there is nothing to compact and the caller keeps uploading the arrays it already holds.
    [Fact]
    public void Compact_ReturnsNullWhenTheSurfaceAlreadyReferencesEveryVertex()
    {
        Assert.Null(SurfaceVertexSlice.Compact(new[] { 0, 1, 2, 2, 1, 0 }, vertexCount: 3));
    }

    [Fact]
    public void Compact_ReturnsNullForAnEmptyPool()
    {
        Assert.Null(SurfaceVertexSlice.Compact(System.Array.Empty<int>(), vertexCount: 0));
        Assert.Null(SurfaceVertexSlice.Compact(System.Array.Empty<int>(), vertexCount: -1));
    }

    // An empty index buffer over a non-empty pool still compacts — to nothing. The mesh builder drops
    // such a surface before it gets here, so this only pins down that the function stays total.
    [Fact]
    public void Compact_ReducesAnEmptyIndexBufferToAnEmptySlice()
    {
        SurfaceVertexSlice.Result slice = Compacted(System.Array.Empty<int>(), vertexCount: 4);

        Assert.Empty(slice.Kept);
        Assert.Empty(slice.Indices);
    }

    // A remap would silently repoint an out-of-range triangle at some other vertex, turning a loud
    // failure into wrong geometry. Refusing to compact leaves the upload path to fail as it does today.
    [Theory]
    [InlineData(4)]
    [InlineData(-1)]
    public void Compact_RefusesASurfaceWithAnOutOfRangeIndex(int stray)
    {
        Assert.Null(SurfaceVertexSlice.Compact(new[] { 0, 1, stray }, vertexCount: 4));
    }

    // The whole change rests on this: slicing every surface of one shared pool has to leave each surface
    // drawing exactly the triangles it drew before, over exactly the vertex data it drew them with.
    // Surface 1 and surface 2 here deliberately SHARE vertex 4 — the material-boundary case — so a slice
    // that dropped a vertex because "the other surface has it" would tear a triangle open.
    [Fact]
    public void Compact_RoundTripsEverySurfaceOfASharedPool()
    {
        var pool = new[] { 100, 101, 102, 103, 104, 105, 106, 107 };
        int[][] surfaces =
        {
            new[] { 0, 1, 2, 2, 1, 3 },
            new[] { 4, 5, 6 },
            new[] { 4, 6, 7 },
        };

        foreach (int[] indices in surfaces)
        {
            SurfaceVertexSlice.Result slice = Compacted(indices, pool.Length);
            int[] channel = SurfaceVertexSlice.Gather(pool, slice.Kept, pool.Length);

            Assert.True(channel.Length < pool.Length, "the slice should be smaller than the pool");
            for (int i = 0; i < indices.Length; i++)
                Assert.Equal(pool[indices[i]], channel[slice.Indices[i]]);
        }
    }

    // Vertex 4 is used by two surfaces, so both slices must contain it.
    [Fact]
    public void Compact_KeepsAVertexTwoSurfacesShareInBothSlices()
    {
        Assert.Contains(4, Compacted(new[] { 4, 5, 6 }, vertexCount: 8).Kept);
        Assert.Contains(4, Compacted(new[] { 4, 6, 7 }, vertexCount: 8).Kept);
    }

    // The map the caller needs to move the surface's OTHER index buffers — its generated LOD levels —
    // onto the same slice. Vertices the slice dropped are marked, not left pointing somewhere plausible.
    [Fact]
    public void Compact_ReportsWhereEverySourceVertexLanded()
    {
        SurfaceVertexSlice.Result slice = Compacted(new[] { 1, 3, 5 }, vertexCount: 6);

        Assert.Equal(
            new[]
            {
                SurfaceVertexSlice.Dropped, 0, SurfaceVertexSlice.Dropped, 1,
                SurfaceVertexSlice.Dropped, 2,
            },
            slice.Remap);
    }

    // A simplified level of the same surface only ever drops vertices, so it moves onto the slice whole.
    [Fact]
    public void Remap_MovesASimplifiedLevelOntoTheSlice()
    {
        SurfaceVertexSlice.Result slice = Compacted(new[] { 4, 5, 6, 6, 5, 7 }, vertexCount: 8);

        Assert.Equal(new[] { 0, 1, 3 }, SurfaceVertexSlice.Remap(new[] { 4, 5, 7 }, slice.Remap));
    }

    // The level and the base have to agree about which vertex is which, or the coarse mesh draws
    // different triangles than the fine one.
    [Fact]
    public void Remap_AgreesWithTheBaseBufferAboutEveryVertex()
    {
        int[] level = { 9, 3, 1 };
        SurfaceVertexSlice.Result slice = Compacted(new[] { 9, 3, 6, 6, 3, 1 }, vertexCount: 10);

        int[] moved = Assert.IsType<int[]>(SurfaceVertexSlice.Remap(level, slice.Remap));
        for (int i = 0; i < level.Length; i++)
            Assert.Equal(level[i], slice.Kept[moved[i]]);
    }

    // The teeth: a level reaching a vertex this surface's slice does not hold must be refused, not
    // clamped or dropped. Clamping would draw a different triangle; dropping would silently coarsen the
    // mesh. The caller answers a refusal by leaving the whole surface unsliced.
    [Fact]
    public void Remap_RefusesALevelThatReachesOutsideTheSlice()
    {
        SurfaceVertexSlice.Result slice = Compacted(new[] { 4, 5, 6 }, vertexCount: 8);

        Assert.Null(SurfaceVertexSlice.Remap(new[] { 4, 5, 7 }, slice.Remap)); // 7 is not in this slice
        Assert.Null(SurfaceVertexSlice.Remap(new[] { 4, 5, 8 }, slice.Remap)); // 8 is not in the pool
        Assert.Null(SurfaceVertexSlice.Remap(new[] { 4, 5, -1 }, slice.Remap));
    }

    [Fact]
    public void Remap_MovesAnEmptyLevelToAnEmptyLevel()
    {
        SurfaceVertexSlice.Result slice = Compacted(new[] { 4, 5, 6 }, vertexCount: 8);

        Assert.Empty(Assert.IsType<int[]>(
            SurfaceVertexSlice.Remap(System.Array.Empty<int>(), slice.Remap)));
    }

    [Fact]
    public void Gather_TakesTheKeptVerticesInOrder()
    {
        var source = new[] { 10, 11, 12, 13, 14 };

        Assert.Equal(new[] { 11, 14 }, SurfaceVertexSlice.Gather(source, new[] { 1, 4 }, source.Length));
    }

    // A channel the mesh does not have (no UVs) or one that does not match the pool is not a channel to
    // gather: passing it through is how "absent" survives, where gathering would invent one.
    [Fact]
    public void Gather_PassesAMismatchedChannelThroughUntouched()
    {
        int[] absent = System.Array.Empty<int>();

        Assert.Same(absent, SurfaceVertexSlice.Gather(absent, new[] { 1, 4 }, vertexCount: 5));
    }

    [Fact]
    public void Gather_ReturnsAnEmptyChannelForAnEmptySlice()
    {
        var source = new[] { 10, 11 };

        Assert.Empty(SurfaceVertexSlice.Gather(source, System.Array.Empty<int>(), source.Length));
    }
}
