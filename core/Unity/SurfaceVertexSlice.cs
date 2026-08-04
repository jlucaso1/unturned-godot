using System.Collections.Generic;

namespace UnturnedGodot.Unity;

// The slice of a cached mesh's shared vertex pool that one uploaded surface actually reaches.
//
// A cached mesh holds ONE vertex/normal/UV pool plus a triangle list per submesh, and the renderer
// uploads one vertex buffer per *surface*. Handing every surface the whole pool therefore uploads that
// pool once per surface while each surface's triangles only ever index its own slice of it. On the
// objects this port places the slices are disjoint and together cover the pool exactly, so a mesh split
// across four materials uploads four copies of a pool each surface uses a quarter of.
//
// `Kept` is ascending in source order. That is not tidiness: mesh simplification resolves vertices that
// share a position to the lowest-numbered one of the set, so keeping the source order keeps that choice
// the same among the vertices a surface retains.
public static class SurfaceVertexSlice
{
    // A source vertex that the slice dropped. Remap never produces one, because a level that reached
    // outside its own surface's slice is refused whole.
    public const int Dropped = -1;

    // The vertices one surface keeps (indices into the source pool, ascending), its triangle list
    // rewritten to address them, and the source-to-slice map that rewrote it. The map is kept because
    // the caller has more index buffers to move onto the slice than the base one — every generated LOD
    // level of the surface indexes the same pool and has to travel with it.
    public readonly record struct Result(int[] Kept, int[] Indices, int[] Remap);

    // Null means "nothing to compact": the surface already references every vertex in the pool, or the
    // pool is empty, or an index is out of range. The caller then uploads the arrays it already holds,
    // which is what keeps the single-surface meshes that are most of any map on an unchanged path.
    public static Result? Compact(IReadOnlyList<int> indices, int vertexCount)
    {
        if (vertexCount <= 0)
            return null;

        var used = new bool[vertexCount];
        int keptCount = 0;
        for (int i = 0; i < indices.Count; i++)
        {
            int vertex = indices[i];
            // An out-of-range index is the caller's problem to diagnose, not this function's to hide: a
            // remap would silently repoint the triangle at a different vertex, so leave the surface
            // exactly as it was and let the upload path fail the same way it does today.
            if ((uint)vertex >= (uint)vertexCount)
                return null;
            if (used[vertex])
                continue;
            used[vertex] = true;
            keptCount++;
        }

        if (keptCount == vertexCount)
            return null;

        var kept = new int[keptCount];
        var remap = new int[vertexCount];
        int next = 0;
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            if (!used[vertex])
            {
                remap[vertex] = Dropped;
                continue;
            }
            remap[vertex] = next;
            kept[next] = vertex;
            next++;
        }

        var local = new int[indices.Count];
        for (int i = 0; i < indices.Count; i++)
            local[i] = remap[indices[i]];

        return new Result(kept, local, remap);
    }

    // One per-vertex channel reduced to the surface's slice. Channels that are absent (a mesh with no UVs)
    // or that do not match the pool are passed straight through, because a length mismatch is exactly how
    // the mesh builder signals "this channel is not present" and gathering it would invent one.
    public static T[] Gather<T>(T[] source, int[] kept, int vertexCount)
    {
        if (source.Length != vertexCount)
            return source;

        var gathered = new T[kept.Length];
        for (int i = 0; i < kept.Length; i++)
            gathered[i] = source[kept[i]];
        return gathered;
    }

    // Another index buffer over the same pool moved onto the slice — a simplified LOD level of the surface
    // the slice was taken from.
    //
    // Null when the buffer reaches a vertex the slice does not hold, or outside the pool entirely. The
    // caller must then leave the whole surface unsliced: dropping the level would silently coarsen the
    // mesh, and clamping the index would draw a different triangle. Simplification only ever removes
    // vertices, so a level of the surface's own chain cannot trip this — the check is here so that a
    // buffer belonging to something else fails loudly rather than quietly.
    public static int[]? Remap(IReadOnlyList<int> indices, int[] remap)
    {
        var moved = new int[indices.Count];
        for (int i = 0; i < indices.Count; i++)
        {
            int vertex = indices[i];
            if ((uint)vertex >= (uint)remap.Length || remap[vertex] == Dropped)
                return null;
            moved[i] = remap[vertex];
        }
        return moved;
    }
}
