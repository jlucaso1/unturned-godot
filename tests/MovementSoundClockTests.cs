using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests;

public class MovementSoundClockTests
{
    private const float Dt = 0.08f;

    private static int CountFootsteps(MovementSoundClock clock, EPlayerStance stance, bool moving,
        bool grounded, float seconds)
    {
        int steps = 0;
        for (float t = 0; t < seconds; t += Dt)
            if (clock.Tick(stance, moving, grounded, PlayerConfig.SpeedFor(stance), Dt) == EMovementSound.Footstep)
                steps++;
        return steps;
    }

    [Fact]
    public void WalkingCadence_MatchesTwoPointOneOverSpeed()
    {
        var clock = new MovementSoundClock(startGrounded: true);
        int steps = CountFootsteps(clock, EPlayerStance.Stand, moving: true, grounded: true, seconds: 4.2f);
        // Interval = 2.1 / 4.5 ≈ 0.467 s -> ~9 steps in 4.2 s.
        Assert.InRange(steps, 8, 10);
    }

    [Fact]
    public void SprintingIsFaster_ProneIsSilent_IdleIsSilent()
    {
        Assert.True(CountFootsteps(new MovementSoundClock(true), EPlayerStance.Sprint, true, true, 3f) >
            CountFootsteps(new MovementSoundClock(true), EPlayerStance.Stand, true, true, 3f));
        Assert.Equal(0, CountFootsteps(new MovementSoundClock(true), EPlayerStance.Prone, true, true, 5f));
        Assert.Equal(0, CountFootsteps(new MovementSoundClock(true), EPlayerStance.Stand, false, true, 5f));
        Assert.Equal(0, CountFootsteps(new MovementSoundClock(true), EPlayerStance.Stand, true, false, 5f));
    }

    [Fact]
    public void Landing_FiresOnceOnTheGroundedEdge_AndReArmsTheFootstepClock()
    {
        var clock = new MovementSoundClock(startGrounded: true);
        for (int i = 0; i < 6; i++) // airborne (a jump)
            Assert.Equal(EMovementSound.None, clock.Tick(EPlayerStance.Stand, false, false, 4.5f, Dt));

        Assert.Equal(EMovementSound.Landed, clock.Tick(EPlayerStance.Stand, false, true, 4.5f, Dt));
        Assert.Equal(EMovementSound.None, clock.Tick(EPlayerStance.Stand, false, true, 4.5f, Dt)); // once

        // The landing reset the cadence: within the 2.1/4.5 ≈ 0.467 s interval (one idle tick already
        // elapsed above) no footstep may fire yet.
        int stepsSoon = 0;
        for (float t = 0; t < 0.3f; t += Dt)
            if (clock.Tick(EPlayerStance.Stand, true, true, 4.5f, Dt) == EMovementSound.Footstep)
                stepsSoon++;
        Assert.Equal(0, stepsSoon);
        for (float t = 0; t < 0.3f; t += Dt) // ...and shortly after, exactly one fires
            if (clock.Tick(EPlayerStance.Stand, true, true, 4.5f, Dt) == EMovementSound.Footstep)
                stepsSoon++;
        Assert.Equal(1, stepsSoon);
    }

    [Fact]
    public void ProneLanding_IsSilent()
    {
        var clock = new MovementSoundClock(startGrounded: true);
        clock.Tick(EPlayerStance.Prone, false, false, 1.5f, Dt); // airborne
        Assert.Equal(EMovementSound.None, clock.Tick(EPlayerStance.Prone, false, true, 1.5f, Dt));
    }

    [Fact]
    public void SpawningInTheAir_ThudsOnFirstGroundContact()
    {
        var clock = new MovementSoundClock(startGrounded: false); // spawn falling, like Unturned
        Assert.Equal(EMovementSound.Landed, clock.Tick(EPlayerStance.Stand, false, true, 4.5f, Dt));
    }
}
