using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests;

public class MeshIndicesTests
{
    [Fact]
    public void Rebase_OffsetsIntoTheCombinedPool_AndKeepsWindingWhenNotMirrored()
    {
        int[] rebased = MeshIndices.Rebase(new[] { 0, 1, 2, 3, 4, 5 }, 10, mirrored: false);

        Assert.Equal(new[] { 10, 11, 12, 13, 14, 15 }, rebased);
    }

    // A mirroring transform reverses which side of a triangle faces out, so the triangle has to be wound
    // the other way or the GPU culls it. Unity does this at draw time off the transform's scale; baking
    // the pose into the vertices moves the job here. Two of every vehicle's four wheels are mirrored,
    // and without this they rendered as a hole with a tyre round it.
    [Fact]
    public void Rebase_ReversesEachTriangleWhenMirrored()
    {
        int[] rebased = MeshIndices.Rebase(new[] { 0, 1, 2, 3, 4, 5 }, 10, mirrored: true);

        Assert.Equal(new[] { 10, 12, 11, 13, 15, 14 }, rebased);
    }

    // Reversing twice is the identity: the fix cannot depend on the triangle's original order.
    [Fact]
    public void Rebase_MirroringTwiceRestoresTheOriginalWinding()
    {
        int[] once = MeshIndices.Rebase(new[] { 7, 8, 9 }, 0, mirrored: true);
        int[] twice = MeshIndices.Rebase(once, 0, mirrored: true);

        Assert.Equal(new[] { 7, 8, 9 }, twice);
    }

    // A malformed index buffer whose length is not a multiple of three keeps its tail rather than losing
    // it here — what counts as an unusable submesh is the caller's call, not this function's.
    [Fact]
    public void Rebase_CopiesATrailingPartialTriangleThrough()
    {
        int[] rebased = MeshIndices.Rebase(new[] { 0, 1, 2, 3, 4 }, 1, mirrored: true);

        Assert.Equal(new[] { 1, 3, 2, 4, 5 }, rebased);
    }

    [Fact]
    public void Rebase_HandlesAnEmptyBuffer()
    {
        Assert.Empty(MeshIndices.Rebase(System.Array.Empty<int>(), 5, mirrored: true));
    }
}
