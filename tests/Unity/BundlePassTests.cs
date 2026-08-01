using System;
using System.Collections.Generic;
using System.Linq;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class BundlePassTests
{
    private static readonly BundlePass.Node[] TwoStreams =
    {
        new("CAB-x.resS", 1000),
        new("CAB-x.resource", 500),
    };

    [Fact]
    public void Plan_ReadsOnlyToTheLastWantedByte()
    {
        // Everything wanted is in the first node, so the pass stops at the end of the last range and the
        // second node is never touched. That trailing .resource is 61 MB on California 2.
        List<BundlePass.Step> steps = BundlePass.Plan(TwoStreams, new[]
        {
            new BundlePass.Want("CAB-x.resS", 100, 50, 0),
            new BundlePass.Want("CAB-x.resS", 600, 40, 1),
        });

        BundlePass.Step step = Assert.Single(steps);
        Assert.Equal(0, step.Node);
        Assert.Equal(640, step.ReadTo);
        Assert.Equal(new[] { 0, 1 }, step.Regions.Select(r => r.Index));
    }

    [Fact]
    public void Plan_NodeWithNothingWanted_IsStillReadWhenALaterOneIsOwed()
    {
        // A forward-only stream cannot skip: the first node has to come out to reach the second.
        List<BundlePass.Step> steps = BundlePass.Plan(TwoStreams, new[]
        {
            new BundlePass.Want("CAB-x.resource", 10, 20, 7),
        });

        Assert.Equal(2, steps.Count);
        Assert.Equal(0, steps[0].Node);
        Assert.Equal(1000, steps[0].ReadTo); // read out in full, nothing taken
        Assert.Empty(steps[0].Regions);

        Assert.Equal(1, steps[1].Node);
        Assert.Equal(30, steps[1].ReadTo);
        Assert.Equal(7, Assert.Single(steps[1].Regions).Index);
    }

    [Fact]
    public void Plan_EarlierNodeIsReadInFull_WhenBothOweSomething()
    {
        List<BundlePass.Step> steps = BundlePass.Plan(TwoStreams, new[]
        {
            new BundlePass.Want("CAB-x.resS", 0, 10, 0),
            new BundlePass.Want("CAB-x.resource", 100, 10, 1),
        });

        Assert.Equal(2, steps.Count);
        Assert.Equal(1000, steps[0].ReadTo); // not just to byte 10: the next node is behind it
        Assert.Equal(110, steps[1].ReadTo);
    }

    [Fact]
    public void Plan_RegionsComeOutInOffsetOrder()
    {
        List<BundlePass.Step> steps = BundlePass.Plan(TwoStreams, new[]
        {
            new BundlePass.Want("CAB-x.resS", 700, 10, 0),
            new BundlePass.Want("CAB-x.resS", 100, 10, 1),
            new BundlePass.Want("CAB-x.resS", 400, 10, 2),
        });

        Assert.Equal(new[] { 1, 2, 0 }, Assert.Single(steps).Regions.Select(r => r.Index));
    }

    [Fact]
    public void Plan_WantNamingAStreamThisBundleDoesNotCarry_IsDropped()
    {
        // This is what kept a pass reading to the very end: a texture can name a .resS belonging to another
        // bundle, and treating that as owed meant every node behind it was decoded for nothing.
        Assert.Empty(BundlePass.Plan(TwoStreams, new[]
        {
            new BundlePass.Want("CAB-other.resS", 0, 10, 0),
        }));
    }

    [Fact]
    public void Plan_WantOutsideItsNode_IsDropped()
    {
        Assert.Empty(BundlePass.Plan(TwoStreams, new[]
        {
            new BundlePass.Want("CAB-x.resS", 990, 20, 0),  // runs past the end of the node
            new BundlePass.Want("CAB-x.resS", -1, 10, 1),   // before the start
            new BundlePass.Want("CAB-x.resS", 10, 0, 2),    // empty
        }));
    }

    [Fact]
    public void Plan_NothingWanted_ReadsNothing()
    {
        Assert.Empty(BundlePass.Plan(TwoStreams, Array.Empty<BundlePass.Want>()));
        Assert.Empty(BundlePass.Plan(Array.Empty<BundlePass.Node>(), new[]
        {
            new BundlePass.Want("CAB-x.resS", 0, 10, 0),
        }));
    }

    [Fact]
    public void Plan_RepeatedFileName_IsServedByTheFirstNode()
    {
        // A bundle does not repeat a stream file, but the plan must not read the same ranges twice if one
        // ever did.
        var nodes = new BundlePass.Node[]
        {
            new("CAB-x.resS", 200),
            new("CAB-x.resS", 200),
        };

        BundlePass.Step step = Assert.Single(BundlePass.Plan(nodes, new[]
        {
            new BundlePass.Want("CAB-x.resS", 20, 10, 0),
        }));

        Assert.Equal(0, step.Node);
        Assert.Equal(30, step.ReadTo);
    }
}
