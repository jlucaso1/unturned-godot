using System;

namespace UnturnedGodot.Unity;

// Index-buffer arithmetic shared by the mesh builders, in core so it can be unit-tested away from the
// bundle reader that uses it.
public static class MeshIndices
{
    // A part's triangles moved into the combined mesh's vertex pool, with their winding reversed when the
    // part was placed through a mirroring transform.
    //
    // Unity keeps a prefab child's scale on its transform and lets the GPU flip the front face when that
    // scale mirrors an axis. Baking the pose into the vertices — which is what makes a prefab's several
    // children one indexed mesh here — loses that, and a mirrored part then has every face wound the wrong
    // way round and culled. A vehicle's left-hand wheels are its right-hand ones scaled by -1, so two
    // wheels of every four rendered as a hole with a tyre round it.
    //
    // A trailing partial triangle (a malformed index buffer) is copied through rather than dropped: the
    // caller decides what an unusable submesh is, and silently changing the count here would move that
    // decision somewhere nothing tests.
    public static int[] Rebase(int[] indices, int baseVertex, bool mirrored)
    {
        var result = new int[indices.Length];
        if (!mirrored)
        {
            for (int i = 0; i < indices.Length; i++)
                result[i] = indices[i] + baseVertex;
            return result;
        }

        int whole = indices.Length - (indices.Length % 3);
        for (int i = 0; i < whole; i += 3)
        {
            result[i] = indices[i] + baseVertex;
            result[i + 1] = indices[i + 2] + baseVertex; // swap the last two: reverses the winding
            result[i + 2] = indices[i + 1] + baseVertex;
        }
        for (int i = whole; i < indices.Length; i++)
            result[i] = indices[i] + baseVertex;
        return result;
    }
}
