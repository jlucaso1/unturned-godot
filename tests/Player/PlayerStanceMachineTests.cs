using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Player;

public class PlayerStanceMachineTests
{
    private static EPlayerStance Resolve(EPlayerStance current, bool crouch = false, bool prone = false,
        bool sprint = false, bool moving = false, bool stamina = true, bool canStand = true, bool canCrouch = true)
        => PlayerStanceMachine.Resolve(current, crouch, prone, sprint, moving, stamina, canStand, canCrouch);

    [Fact]
    public void CrouchIntent_DropsFromStand()
        => Assert.Equal(EPlayerStance.Crouch, Resolve(EPlayerStance.Stand, crouch: true));

    [Fact]
    public void ProneIntent_AlwaysDropsToProne()
        => Assert.Equal(EPlayerStance.Prone, Resolve(EPlayerStance.Stand, prone: true));

    [Fact]
    public void ReleasingCrouch_StandsUp_WhenHeadroom()
        => Assert.Equal(EPlayerStance.Stand, Resolve(EPlayerStance.Crouch));

    [Fact]
    public void ReleasingCrouch_StaysCrouched_UnderLowCeiling()
        => Assert.Equal(EPlayerStance.Crouch, Resolve(EPlayerStance.Crouch, canStand: false));

    [Fact]
    public void RaisingProneToCrouch_NeedsCrouchHeadroom()
    {
        Assert.Equal(EPlayerStance.Crouch, Resolve(EPlayerStance.Prone, crouch: true, canCrouch: true));
        Assert.Equal(EPlayerStance.Prone, Resolve(EPlayerStance.Prone, crouch: true, canCrouch: false));
    }

    [Fact]
    public void Sprint_OnlyFromMovingStandWithStamina()
    {
        Assert.Equal(EPlayerStance.Sprint, Resolve(EPlayerStance.Stand, sprint: true, moving: true));
        Assert.Equal(EPlayerStance.Stand, Resolve(EPlayerStance.Stand, sprint: true, moving: false)); // idle
        Assert.Equal(EPlayerStance.Stand, Resolve(EPlayerStance.Stand, sprint: true, moving: true, stamina: false));
    }

    [Fact]
    public void Sprint_StopsBackToStand_WhenReleased()
        => Assert.Equal(EPlayerStance.Stand, Resolve(EPlayerStance.Sprint, sprint: false, moving: true));

    [Fact]
    public void CrouchIntent_WhileSprinting_Crouches()
        => Assert.Equal(EPlayerStance.Crouch, Resolve(EPlayerStance.Sprint, crouch: true, moving: true));

    [Theory]
    [InlineData(EPlayerStance.Stand, false, false, false)]
    [InlineData(EPlayerStance.Sprint, false, false, false)]
    [InlineData(EPlayerStance.Crouch, true, false, false)]
    [InlineData(EPlayerStance.Prone, true, false, false)]
    [InlineData(EPlayerStance.Crouch, false, true, false)]
    [InlineData(EPlayerStance.Prone, false, true, false)]
    [InlineData(EPlayerStance.Crouch, false, false, true)]
    [InlineData(EPlayerStance.Prone, false, false, true)]
    public void StandClearance_IsRequestedOnlyWhenActuallyRaising(
        EPlayerStance current, bool crouch, bool prone, bool expected)
        => Assert.Equal(expected, PlayerStanceMachine.NeedsStandClearance(current, crouch, prone));

    [Theory]
    [InlineData(EPlayerStance.Stand, true, false)]
    [InlineData(EPlayerStance.Crouch, true, false)]
    [InlineData(EPlayerStance.Prone, false, false)]
    [InlineData(EPlayerStance.Prone, true, true)]
    public void CrouchClearance_IsRequestedOnlyWhenRaisingFromProne(
        EPlayerStance current, bool crouch, bool expected)
        => Assert.Equal(expected, PlayerStanceMachine.NeedsCrouchClearance(current, crouch));
}
