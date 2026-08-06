using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Player;

// Which state the first-person arms play while a gesture is on them.
//
// This is the rule behind a real bug: punching while CROUCHED played the swing and showed nothing. A
// gesture is mixed onto the shoulders alone, so the rest of the arm is placed by the state clip
// underneath it, and the crouch idle folds the body down and forward far enough to take the swing out of
// the view. PlayerAnimator stands the first-person rig up for the length of any gesture, and only that
// rig — which is exactly what makes the punch visible from every stance.
public class ViewmodelStateTests
{
    // The stance that was broken, and the one the fix is really for.
    [Fact]
    public void ACrouchedPunchIsPlayedStanding()
    {
        Assert.Equal((EPlayerStance.Stand, false),
            ViewmodelState.For(EPlayerStance.Crouch, moving: false, gesturePlaying: true));
    }

    // Every stance a gesture can be thrown from, not only the crouch: the same folding applies to a
    // swimming or climbing idle, and a rule that only special-cased the crouch would leave those broken
    // in the same way.
    [Theory]
    [InlineData(EPlayerStance.Stand)]
    [InlineData(EPlayerStance.Sprint)]
    [InlineData(EPlayerStance.Crouch)]
    [InlineData(EPlayerStance.Prone)]
    [InlineData(EPlayerStance.Climb)]
    public void AnyStanceIsPlayedStandingWhileAGestureRuns(EPlayerStance stance)
    {
        Assert.Equal((EPlayerStance.Stand, false),
            ViewmodelState.For(stance, moving: true, gesturePlaying: true));
    }

    // The walk bob stops too. `state("Idle_Stand")` names ONE clip, so a player punching while walking
    // gets the standing idle rather than the standing walk.
    [Fact]
    public void MovingStopsForTheLengthOfTheGesture()
    {
        Assert.Equal((EPlayerStance.Stand, false),
            ViewmodelState.For(EPlayerStance.Stand, moving: true, gesturePlaying: true));
    }

    // With nothing playing, the arms follow the player exactly — they idle and bob with the body.
    [Theory]
    [InlineData(EPlayerStance.Crouch, true)]
    [InlineData(EPlayerStance.Crouch, false)]
    [InlineData(EPlayerStance.Sprint, true)]
    [InlineData(EPlayerStance.Prone, false)]
    public void WithNoGestureTheArmsFollowThePlayer(EPlayerStance stance, bool moving)
    {
        Assert.Equal((stance, moving), ViewmodelState.For(stance, moving, gesturePlaying: false));
    }
}
