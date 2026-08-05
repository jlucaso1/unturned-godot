using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Player;

// The port of Unity's AddMixingTransform(transform, recursive: true): which bones a layer-1 clip may
// write. Getting this wrong is not a crash, it is a punch that either does nothing or stops the legs.
public class BoneMaskTests
{
    // 0 -> 1 -> 2, and a separate root 3 -> 4.
    private static readonly int[] TwoTrees = { -1, 0, 1, -1, 3 };

    [Fact]
    public void ASubtreeIsTheRootAndEverythingUnderIt()
    {
        BoneMask mask = BoneMask.Subtrees(TwoTrees, new[] { 0 })!;

        Assert.True(mask.Contains(0));
        Assert.True(mask.Contains(1));
        Assert.True(mask.Contains(2)); // recursive, not just the immediate children
        Assert.False(mask.Contains(3));
        Assert.False(mask.Contains(4));
    }

    [Fact]
    public void SeveralRootsAreTheUnionOfTheirSubtrees()
    {
        BoneMask mask = BoneMask.Subtrees(TwoTrees, new[] { 1, 3 })!;

        Assert.False(mask.Contains(0));
        Assert.True(mask.Contains(1));
        Assert.True(mask.Contains(2));
        Assert.True(mask.Contains(3));
        Assert.True(mask.Contains(4));
    }

    // Unturned's `mixAnimation(name)` overload adds no mixing transforms at all, which in Unity restricts
    // nothing. The port spells that as no mask rather than a mask holding every bone, so the difference
    // between "the whole body" and "a rig whose shoulders could not be found" stays visible.
    [Fact]
    public void NoRootsIsNoMaskAtAll()
    {
        Assert.Null(BoneMask.Subtrees(TwoTrees, System.Array.Empty<int>()));
    }

    // The bone array is the renderer's, whose order nothing guarantees: a child may precede its parent.
    // Resolving by walking UP from each bone is what makes the result independent of that order.
    [Fact]
    public void AChildListedBeforeItsParentIsStillIncluded()
    {
        // 0's parent is 1, 1's parent is 2, 2 is the root — the hierarchy written backwards.
        BoneMask mask = BoneMask.Subtrees(new[] { 1, 2, -1 }, new[] { 2 })!;

        Assert.True(mask.Contains(0));
        Assert.True(mask.Contains(1));
        Assert.True(mask.Contains(2));
    }

    [Fact]
    public void ABoneOutsideTheRigIsNotInTheMask()
    {
        BoneMask mask = BoneMask.Subtrees(TwoTrees, new[] { 0 })!;

        Assert.Equal(5, mask.BoneCount);
        Assert.False(mask.Contains(-1));
        Assert.False(mask.Contains(5));
        Assert.False(mask.Contains(int.MaxValue));
    }

    // A malformed parent chain must not hang the build. Rigs this port makes are trees, but the guard is
    // the step count rather than the shape, so a cycle resolves to "not under the root" and returns.
    [Fact]
    public void ACyclicParentChainTerminates()
    {
        BoneMask mask = BoneMask.Subtrees(new[] { 1, 0, -1 }, new[] { 2 })!;

        Assert.True(mask.Contains(2));
        Assert.False(mask.Contains(0));
        Assert.False(mask.Contains(1));
    }
}
