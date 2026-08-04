using System.Collections.Generic;
using Godot;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

// Where a SkinnedMeshRenderer's mesh renders, relative to its prefab root. The walk that supplies the
// inputs is checked against the shipped bundle in PrefabGraphRealDataTests; this pins the decision itself,
// including the two cases the official content has no example of.
public class SkinnedLocalToRootTests
{
    private static readonly Transform3D Chain =
        new(Basis.Identity.Rotated(Vector3.Right, Mathf.Pi / 2), new Vector3(1, 2, 3));

    private static Dictionary<string, object> Renderer(int bones)
    {
        var list = new List<object>();
        for (int i = 0; i < bones; i++)
            list.Add(new Dictionary<string, object> { ["m_FileID"] = 0, ["m_PathID"] = 100L + i });
        return new Dictionary<string, object> { ["m_Bones"] = list };
    }

    // A skeleton an Animation drives is not standing where the saved prefab left it — that component is
    // there to overwrite those poses — so what renders is the bind pose, and the vertices are already in it.
    [Fact]
    public void AnimatedSkeleton_RendersAtTheBindPose() =>
        Assert.Equal(Transform3D.Identity,
            PrefabGraph.SkinnedLocalToRoot(Renderer(1), Chain, animated: true));

    // Nothing drives it, so the bones stay where the prefab put them and the renderer's own chain applies —
    // the Tank's and the Explorer's skinned parts, which sit well off their prefab roots.
    [Fact]
    public void UndrivenSkeleton_KeepsTheGameObjectChain() =>
        Assert.Equal(Chain, PrefabGraph.SkinnedLocalToRoot(Renderer(2), Chain, animated: false));

    // No bones and no m_Bones at all: nothing can pose these vertices, so Unity places them by the
    // renderer's transform like any other mesh. A blend-shape-only renderer, or a mod's unskinned export.
    [Fact]
    public void NoBones_KeepsTheGameObjectChain() =>
        Assert.Equal(Chain, PrefabGraph.SkinnedLocalToRoot(Renderer(0), Chain, animated: true));

    [Fact]
    public void MissingBonesField_KeepsTheGameObjectChain() =>
        Assert.Equal(Chain, PrefabGraph.SkinnedLocalToRoot(
            new Dictionary<string, object>(), Chain, animated: true));
}
