using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Assets;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The mark a hit leaves, as a node: how it is oriented against the surface, and what keeps a session from
// accumulating decals without bound.
//
// Orientation is the part worth pinning. A Godot Decal projects down its own -Y, so the mark lands on the
// surface only when the basis puts +Y along the normal — get that wrong and every punch on a wall paints
// the floor beside it instead, which looks like the feature not working at all.
public class ImpactDecalsTests : TestClass
{
    public ImpactDecalsTests(Node testScene) : base(testScene) { }

    private static ImpactDecals Build() =>
        new(new PhysicsMaterialBank(), new ImpactEffectBank(), _ => "core", "/nowhere");

    // The projection axis is the normal, whichever way the surface faces.
    [Test]
    public void TheProjectionAxisIsTheSurfaceNormal()
    {
        foreach (Vector3 normal in new[]
                 {
                     Vector3.Up, Vector3.Down, Vector3.Left, Vector3.Right,
                     Vector3.Forward, Vector3.Back, new Vector3(1f, 1f, 1f).Normalized(),
                 })
        {
            Basis basis = ImpactDecals.FacingBasis(normal, 0f);

            Assert.True(basis.Y.Normalized().Dot(normal.Normalized()) > 0.999f,
                $"a decal on {normal} projects along {basis.Y}");
        }
    }

    // A floor and a ceiling are exactly the degenerate case for a cross product against Vector3.Up, and
    // both are ordinary surfaces to punch.
    [Test]
    public void TheBasisStaysWellFormedOnAFloorAndACeiling()
    {
        foreach (Vector3 normal in new[] { Vector3.Up, Vector3.Down })
        {
            Basis basis = ImpactDecals.FacingBasis(normal, 0f);

            Assert.True(basis.Determinant() > 0.99f,
                $"a decal on {normal} has a degenerate basis (det {basis.Determinant()})");
            Assert.True(Mathf.IsEqualApprox(basis.X.Length(), 1f, 0.001f));
            Assert.True(Mathf.IsEqualApprox(basis.Z.Length(), 1f, 0.001f));
        }
    }

    // The spin varies the mark without moving it off the surface — repeated punches on one wall should
    // not stamp an identical shape.
    [Test]
    public void SpinningTheMarkKeepsItOnTheSurface()
    {
        Basis unspun = ImpactDecals.FacingBasis(Vector3.Back, 0f);
        Basis spun = ImpactDecals.FacingBasis(Vector3.Back, Mathf.Pi / 3f);

        Assert.True(spun.Y.Dot(unspun.Y) > 0.999f, "the spin tilted the projection");
        Assert.True(spun.X.Dot(unspun.X) < 0.99f, "the spin did not actually rotate the mark");
    }

    [Test]
    public void AZeroNormalDoesNotProduceAnInvalidBasis()
    {
        Basis basis = ImpactDecals.FacingBasis(Vector3.Zero, 0f);

        Assert.True(basis.Determinant() > 0.99f);
    }

    // With no texture cache to read, every surface is unmarked — and that is a counted outcome rather
    // than a crash, because a session whose extraction has not finished yet is in exactly this state.
    [Test]
    public void WithNoCachedTexturesNothingIsMarked()
    {
        ImpactDecals decals = Build();
        TestScene.AddChild(decals);

        Assert.False(decals.Mark("Wood_Static", Vector3.Zero, Vector3.Up));
        Assert.False(decals.Mark("", Vector3.Zero, Vector3.Up));
        Assert.Equal(2, decals.UnmarkedImpacts);

        decals.QueueFree();
    }

    // The pool is a real ceiling: a Decal is a projection pass, and a punch every half second for the 48
    // seconds a mark lives would otherwise leave ~96 of them standing.
    [Test]
    public void ThePoolIsBounded()
    {
        ImpactDecals decals = Build();
        TestScene.AddChild(decals);

        for (int i = 0; i < ImpactDecals.MaxDecals * 3; i++)
            decals.Claim();

        var found = new List<Decal>();
        foreach (Node child in decals.GetChildren())
            if (child is Decal decal)
                found.Add(decal);

        Assert.Equal(ImpactDecals.MaxDecals, found.Count);

        decals.QueueFree();
    }

    // Once the pool is full, the next mark takes the one that expires soonest — so a burst of punches
    // replaces the oldest marks rather than the same one over and over.
    [Test]
    public void AFullPoolRecyclesRatherThanReusingOneSlot()
    {
        ImpactDecals decals = Build();
        TestScene.AddChild(decals);

        var claimed = new List<Decal>();
        for (int i = 0; i < ImpactDecals.MaxDecals; i++)
            claimed.Add(decals.Claim());

        var recycled = new HashSet<Decal>();
        for (int i = 0; i < ImpactDecals.MaxDecals; i++)
            recycled.Add(decals.Claim());

        Assert.Equal(ImpactDecals.MaxDecals, recycled.Count);
        foreach (Decal decal in claimed)
            Assert.Contains(decal, recycled);

        decals.QueueFree();
    }

    // The ORDER the ring recycles in, pinned rather than described.
    //
    // The round-robin was a deliberate choice — recycling "whichever expires soonest" was the first
    // attempt and was wrong, because each mark's lifetime carries a random spread and the expiry order
    // is therefore not the creation order. Anything that quietly moved which slot comes round first
    // would be a behaviour change wearing an optimization's clothes, so the sequence is asserted.
    //
    // Note what it says: `_next` advances BEFORE it is used, so a freshly filled pool comes back round
    // to slot 1 first and slot 0 waits a full lap. That predates the live-count bookkeeping and is not
    // changed by it — it is written down here so that any future change to it has to be deliberate.
    [Test]
    public void TheRingRecyclesInACompleteLapStartingAtTheSecondSlot()
    {
        ImpactDecals decals = Build();
        TestScene.AddChild(decals);

        var claimed = new List<Decal>();
        for (int i = 0; i < ImpactDecals.MaxDecals; i++)
            claimed.Add(decals.Claim());

        for (int i = 0; i < ImpactDecals.MaxDecals; i++)
        {
            // 1, 2, ... 23, then back to 0: one full lap, every slot exactly once, in ring order.
            Decal expected = claimed[(i + 1) % ImpactDecals.MaxDecals];
            Assert.Same(expected, decals.Claim());
        }

        decals.QueueFree();
    }

    // The sweep retires on the pool's own bookkeeping rather than on what the engine says each slot is
    // doing. It used to read Decal.Visible — a marshalled property — across all 24 slots plus a
    // Time.GetTicksMsec every frame, which is ~3,500 interop crossings a second at 144 fps to establish
    // that nothing had expired. What is asserted here is that the answer did not move with the source.
    [Test]
    public async Task MarksRetireWhenTheirTimeIsUp()
    {
        ImpactDecals decals = Build();
        TestScene.AddChild(decals);

        // A pool nobody has punched into is the state a session spends nearly all of its time in, and it
        // is the one the sweep now skips outright.
        await NextFrame();
        Assert.Equal(0, decals.LiveDecals);

        var claimed = new List<Decal>();
        for (int i = 0; i < 3; i++)
        {
            Decal decal = decals.Claim();
            decal.Visible = true; // what Mark does once it has a texture
            claimed.Add(decal);
        }

        await NextFrame();
        Assert.Equal(3, decals.LiveDecals); // nothing is 48 seconds old yet
        foreach (Decal decal in claimed)
            Assert.True(decal.Visible);

        decals.AgeForTest(ImpactDecals.LifetimeSeconds + ImpactDecals.LifetimeSpread + 1.0);
        await NextFrame();

        Assert.Equal(0, decals.LiveDecals);
        foreach (Decal decal in claimed)
            Assert.False(decal.Visible, "an expired mark is still being drawn");

        // And a retired slot is claimable again, which is what would break if the count and the flags
        // ever fell out of step.
        decals.Claim().Visible = true;
        Assert.Equal(1, decals.LiveDecals);

        decals.QueueFree();
    }

    private SignalAwaiter NextFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
}
