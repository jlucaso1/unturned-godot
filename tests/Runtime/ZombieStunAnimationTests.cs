using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Which clip owns a zombie's rig when a stagger, a wake-up roar and a swing all want it.
//
// This is where the stun was visibly broken. The stagger and the startle used ONE field to say "a one-shot
// is playing", and they arrive independently — a stun is reliable, a state snapshot is not — so a wake-up
// landing in the same frame as a stagger simply overwrote it: the zombie roared mid-stagger and kept
// walking. The fix is not a state machine but a rule: a stagger interrupts, a startle waits its turn.
public class ZombieStunAnimationTests : TestClass
{
    public ZombieStunAnimationTests(Node testScene) : base(testScene) { }

    private ZombiesView View()
    {
        var view = new ZombiesView();
        TestScene.AddChild(view);
        return view;
    }

    // The plain case: a stagger takes the rig and holds it.
    [Test]
    public void AStaggerHoldsTheRig()
    {
        ZombiesView view = View();
        view.PlantForTest(Vector3.Zero);

        view.StunForTest(1, clip: 0, now: 100.0);

        Assert.True(view.HoldRemainingForTest(1, now: 100.0) > 0.0);

        view.QueueFree();
    }

    // The race that was losing: a wake-up arriving while the stagger runs must not take the rig. The
    // zombie has already been hit; roaring over the stagger reads as a second reaction to it.
    [Test]
    public void AWakeUpDuringAStaggerDoesNotStealTheRig()
    {
        ZombiesView view = View();
        view.PlantForTest(Vector3.Zero);
        view.StunForTest(1, clip: 0, now: 100.0);
        double held = view.HoldRemainingForTest(1, now: 100.0);

        // A state snapshot arriving in the same frame, waking the zombie into a chase.
        view.PushStateForTest(1, EZombieState.Chase, now: 100.0);

        Assert.Equal(held, view.HoldRemainingForTest(1, now: 100.0), 3);

        view.QueueFree();
    }

    // The other direction: a stagger DOES take the rig from a wake-up already playing. A zombie hit hard
    // enough to stagger, mid-roar, staggers.
    [Test]
    public void AStaggerInterruptsAWakeUp()
    {
        ZombiesView view = View();
        view.PlantForTest(Vector3.Zero);
        view.PushStateForTest(1, EZombieState.Chase, now: 100.0);
        double startle = view.HoldRemainingForTest(1, now: 100.0);

        view.StunForTest(1, clip: 0, now: 100.5);

        // A fresh hold, measured from the stagger rather than what was left of the roar.
        Assert.NotEqual(startle - 0.5, view.HoldRemainingForTest(1, now: 100.5), 3);
        Assert.True(view.HoldRemainingForTest(1, now: 100.5) > 0.0);

        view.QueueFree();
    }

    // A swing announced while the stagger runs is dropped rather than queued: the server keeps sending
    // Attack, so the per-frame loop starts one as soon as the rig is free. Queuing it would play a swing
    // the zombie is no longer making — and the server has already left the Attack state anyway.
    [Test]
    public void ASwingAnnouncedDuringAStaggerIsNotQueued()
    {
        ZombiesView view = View();
        view.PlantForTest(Vector3.Zero);
        view.StunForTest(1, clip: 0, now: 100.0);
        double held = view.HoldRemainingForTest(1, now: 100.0);

        view.PushStateForTest(1, EZombieState.Attack, now: 100.0);

        Assert.Equal(held, view.HoldRemainingForTest(1, now: 100.0), 3);

        view.QueueFree();
    }

    // Two staggers in a row restart the hold rather than stacking, matching the server's own clock.
    [Test]
    public void ASecondStaggerRestartsTheHold()
    {
        ZombiesView view = View();
        view.PlantForTest(Vector3.Zero);

        view.StunForTest(1, clip: 0, now: 100.0);
        view.StunForTest(1, clip: 1, now: 100.5);

        Assert.Equal(ZombieStun.DurationSeconds, view.HoldRemainingForTest(1, now: 100.5), 3);

        view.QueueFree();
    }

    // The state underneath keeps being recorded while the stagger plays: it is what the rig goes back to
    // when the reel ends, so dropping it would leave the body idling after a stagger it was chasing into.
    [Test]
    public void TheStateKeepsBeingRecordedUnderneathAStagger()
    {
        ZombiesView view = View();
        view.PlantForTest(Vector3.Zero);
        view.StunForTest(1, clip: 0, now: 100.0);

        view.PushStateForTest(1, EZombieState.Attack, now: 100.0);

        Assert.Equal(EZombieState.Attack, view.StateForTest(1));

        view.QueueFree();
    }

    // A stagger for a zombie this client never heard of is dropped rather than throwing: a stun can
    // legitimately arrive before the region list that would have spawned the avatar.
    [Test]
    public void AStaggerForAnUnknownZombieIsHarmless()
    {
        ZombiesView view = View();

        view.StunForTest(99, clip: 0, now: 100.0);

        Assert.Equal(0.0, view.HoldRemainingForTest(99, now: 100.0));

        view.QueueFree();
    }
}
