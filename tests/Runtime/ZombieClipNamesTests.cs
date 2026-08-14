using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Which clip name a zombie's replicated Move/Idle/Speciality resolve to, and that re-entering a region
// leaves an avatar looking like a fresh spawn rather than like the one that walked out.
//
// The names used to be built with $"Move_{avatar.Move}" every frame, for every streaming avatar, only for
// CharacterSkeleton.Play's already-playing early-out to drop the string it had just allocated. They come
// off a table now, which is a change of allocation and not of answer — so what these assert first is that
// every index still maps where it did.
public class ZombieClipNamesTests : TestClass
{
    public ZombieClipNamesTests(Node testScene) : base(testScene) { }

    private ZombiesView View()
    {
        var view = new ZombiesView();
        TestScene.AddChild(view);
        return view;
    }

    // Zombie.cs's own mapping, spelled out: the rolled variant for an ordinary zombie and a mega, and the
    // fixed override each of the two specialities that has one carries.
    [Test]
    public void EveryReplicatedVariantResolvesToTheNameItAlwaysDid()
    {
        for (byte move = 0; move < 4; move++)
        {
            Assert.Equal($"Move_{move}", ZombiesView.MoveClipFor(EZombieSpeciality.Normal, move));
            Assert.Equal($"Move_{move}", ZombiesView.MoveClipFor(EZombieSpeciality.Mega, move));
        }

        for (byte idle = 0; idle < 3; idle++)
        {
            Assert.Equal($"Idle_{idle}", ZombiesView.IdleClipFor(EZombieSpeciality.Normal, idle));
            Assert.Equal($"Idle_{idle}", ZombiesView.IdleClipFor(EZombieSpeciality.Mega, idle));
        }

        // Crawlers and sprinters ignore the roll entirely (Move_4/Idle_3 and Move_5/Idle_4).
        Assert.Equal("Move_4", ZombiesView.MoveClipFor(EZombieSpeciality.Crawler, 2));
        Assert.Equal("Idle_3", ZombiesView.IdleClipFor(EZombieSpeciality.Crawler, 1));
        Assert.Equal("Move_5", ZombiesView.MoveClipFor(EZombieSpeciality.Sprinter, 2));
        Assert.Equal("Idle_4", ZombiesView.IdleClipFor(EZombieSpeciality.Sprinter, 1));
    }

    // The same indices give the same REFERENCE, which is the point of the table: the string a rig is
    // handed each frame is the one it was handed last frame, so Play's comparison is a reference hit
    // and no frame allocates to reach it.
    [Test]
    public void TheSameVariantHandsBackTheSameString()
    {
        Assert.Same(ZombiesView.MoveClipFor(EZombieSpeciality.Normal, 2),
            ZombiesView.MoveClipFor(EZombieSpeciality.Normal, 2));
        Assert.Same(ZombiesView.IdleClipFor(EZombieSpeciality.Normal, 1),
            ZombiesView.IdleClipFor(EZombieSpeciality.Normal, 1));
    }

    // Move and Idle arrive off the wire, so a server sending a variant no rig carries must not index
    // past the table. The old form built "Move_9", which Play then dropped for want of a clip; the
    // clamp lands on a real name instead, and neither answer can take the frame down.
    [Test]
    public void AVariantOutsideTheRolledRangeIsClamped()
    {
        Assert.Equal("Move_3", ZombiesView.MoveClipFor(EZombieSpeciality.Normal, 9));
        Assert.Equal("Idle_2", ZombiesView.IdleClipFor(EZombieSpeciality.Normal, 255));
    }

    // The avatar carries the resolved names, refreshed where the listing sets the three fields they
    // depend on — that is what lets the per-frame loop read a field instead of running the switch.
    [Test]
    public void AListingResolvesTheAvatarsClipsUpFront()
    {
        ZombiesView view = View();

        view.ListForTest(Listing(id: 7, move: 2, idle: 1), bound: 0);
        Assert.Equal("Move_2", view.MoveClipForTest(7));
        Assert.Equal("Idle_1", view.IdleClipForTest(7));

        // Re-listed as a crawler: the override has to take over, or the avatar keeps walking with the
        // clip its previous speciality resolved to.
        view.ListForTest(
            Listing(id: 7, move: 2, idle: 1, speciality: EZombieSpeciality.Crawler), bound: 0);
        Assert.Equal("Move_4", view.MoveClipForTest(7));
        Assert.Equal("Idle_3", view.IdleClipForTest(7));

        view.QueueFree();
    }

    // Re-entering a region is a fresh meeting with the zombie, so the clocks reset with the pose.
    //
    // Leaving them was visible twice. A stale State of Attack makes Push early-return on the server's
    // very next state for this zombie, so the forced idle stands until NextSwing comes round instead of
    // the swing resuming; and a HoldUntil left over from a stagger that finished while the avatar was
    // out of the region suppresses clip selection outright until that instant passes.
    [Test]
    public void ReEnteringTheRegionClearsTheClocksTheAvatarLeftWith()
    {
        ZombiesView view = View();
        view.ListForTest(Listing(id: 9), bound: 0);

        // What the avatar was doing when the player walked out: mid-attack, and staggered.
        view.PushStateForTest(9, EZombieState.Attack, now: 100.0);
        view.StunForTest(9, clip: 0, now: 100.0);
        Assert.Equal(EZombieState.Attack, view.StateForTest(9));
        Assert.True(view.HoldRemainingForTest(9, now: 100.0) > 0.0);

        // And what the region list says on the way back in.
        view.ListForTest(Listing(id: 9), bound: 0);

        Assert.Equal(EZombieState.Idle, view.StateForTest(9));
        Assert.Equal(0.0, view.HoldRemainingForTest(9, now: 100.0));

        // The proof that State really was cleared rather than merely read as Idle: the server's next
        // Attack is acted on instead of early-returning against a stale copy of itself.
        view.PushStateForTest(9, EZombieState.Attack, now: 101.0);
        Assert.Equal(EZombieState.Attack, view.StateForTest(9));

        view.QueueFree();
    }

    private static ZombieListing Listing(ushort id, byte move = 0, byte idle = 0,
        EZombieSpeciality speciality = EZombieSpeciality.Normal) => new()
        {
            Id = id,
            Speciality = speciality,
            Move = move,
            Idle = idle,
            Position = Vector3.Zero,
        };
}
