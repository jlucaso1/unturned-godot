using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Zombies;

public class ZombieCollisionDetourTests
{
    [Fact]
    public void AnOpenDirectSegmentUsesOneProbeAndNoSearchGrid()
    {
        var planner = new ZombieCollisionDetour();
        var path = new List<Vector3>();
        var from = new Vector3(-2f, 0f, 0f);
        var target = new Vector3(2f, 0f, 0f);

        Assert.True(planner.TryFind(from, target, 0.4f, Ground,
            (_, _, _) => true, path));
        Assert.Equal(new[] { from, target }, path);
        Assert.Equal(1, planner.LastProbeCount);
    }

    [Fact]
    public void AFiniteInFaceWallGetsADeterministicPhysicallyClearDetour()
    {
        var planner = new ZombieCollisionDetour();
        var first = new List<Vector3>();
        var second = new List<Vector3>();
        var from = new Vector3(-2f, 0f, 0f);
        var target = new Vector3(2f, 0f, 0f);

        Assert.True(planner.TryFind(from, target, 0.4f, Ground, SegmentClear, first));
        Assert.InRange(planner.LastProbeCount, 1, ZombieCollisionDetour.MaxEdgeProbes);
        Assert.True(first.Count > 2, "the physically blocked direct segment was returned");
        Assert.Equal(from, first[0]);
        Assert.Equal(target, first[^1]);
        for (int i = 1; i < first.Count; i++)
            Assert.True(SegmentClear(first[i - 1], first[i], 0.4f),
                $"unproven edge {first[i - 1]} -> {first[i]}");

        Assert.True(planner.TryFind(from, target, 0.4f, Ground, SegmentClear, second));
        Assert.Equal(first, second);
    }

    [Fact]
    public void AFenceLongerThanTheLocalSearchCannotBeCrossedOrWalkedIndefinitely()
    {
        var planner = new ZombieCollisionDetour();
        var path = new List<Vector3>();
        var from = new Vector3(-2f, 0f, 0f);
        var target = new Vector3(2f, 0f, 0f);

        Assert.False(planner.TryFind(from, target, 0.4f, Ground,
            (a, b, radius) => SegmentClear(a, b, radius, halfLength: 20f), path));
        Assert.Empty(path);
        Assert.Equal(ZombieCollisionDetour.MaxEdgeProbes, planner.LastProbeCount);
    }

    [Fact]
    public void ADeepCounterExitWithinTheFourteenMetreBoundCanStillBeReached()
    {
        var planner = new ZombieCollisionDetour();
        var path = new List<Vector3>();
        var from = new Vector3(-2f, 0f, 0f);
        var target = new Vector3(2f, 0f, 0f);

        Assert.True(planner.TryFind(from, target, 0.4f, Ground,
            (a, b, radius) => SegmentClear(a, b, radius, halfLength: 12f), path));
        Assert.True(path.Exists(p => MathF.Abs(p.Z) > 12.4f),
            $"route did not go around the deep obstacle: {string.Join(" ", path)}");
        Assert.InRange(planner.LastProbeCount, 1, ZombieCollisionDetour.MaxEdgeProbes);
    }

    private static bool Ground(Vector3 point, out Vector3 grounded)
    {
        grounded = point;
        return true;
    }

    private static bool SegmentClear(Vector3 from, Vector3 to, float radius) =>
        SegmentClear(from, to, radius, halfLength: 4f);

    // A zero-thickness wall on x=0, expanded by the body radius exactly as a capsule sweep sees it.
    private static bool SegmentClear(Vector3 from, Vector3 to, float radius, float halfLength)
    {
        float dx = to.X - from.X;
        if (MathF.Abs(dx) <= 1e-6f || from.X * to.X > 0f)
            return true;
        float along = -from.X / dx;
        if (along is < 0f or > 1f)
            return true;
        float z = from.Z + ((to.Z - from.Z) * along);
        return MathF.Abs(z) > halfLength + radius;
    }
}
