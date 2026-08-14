using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

public class LightingCycleTests
{
    private const float Bias = 0.5f;
    private const float Fade = 0.5f; // -> transition = 0.125

    [Theory]
    [InlineData(0.5f, 1f, 0.25f)]   // symmetric day/night: bias/2 * fade
    [InlineData(0.3f, 1f, 0.15f)]   // short day: bias/2 bounds the window
    [InlineData(0.8f, 1f, 0.10f)]   // short night: (1-bias)/2 bounds it
    [InlineData(0.5f, 0.5f, 0.125f)]
    public void Transition_UsesShorterHalfScaledByFade(float bias, float fade, float expected)
    {
        Assert.Equal(expected, LightingCycle.Transition(bias, fade), 5);
    }

    [Theory]
    [InlineData(0.0f, LightingTime.Dawn, LightingTime.Midday, 0f)]      // dawn anchor
    [InlineData(0.0625f, LightingTime.Dawn, LightingTime.Midday, 0.5f)] // halfway into morning fade
    [InlineData(0.25f, LightingTime.Midday, LightingTime.Midday, 0f)]   // steady midday
    [InlineData(0.45f, LightingTime.Midday, LightingTime.Dusk, 0.6f)]   // approaching dusk
    [InlineData(0.5f, LightingTime.Dusk, LightingTime.Midnight, 0f)]    // dusk anchor (= bias)
    [InlineData(0.55f, LightingTime.Dusk, LightingTime.Midnight, 0.4f)]
    [InlineData(0.75f, LightingTime.Midnight, LightingTime.Midnight, 0f)] // steady midnight
    [InlineData(0.95f, LightingTime.Midnight, LightingTime.Dawn, 0.6f)]   // pre-dawn fade
    public void Blend_MatchesGetLightingIndices(float time, LightingTime from, LightingTime to, float alpha)
    {
        (LightingTime f, LightingTime t, float a) =
            LightingCycle.Blend(time, Bias, LightingCycle.Transition(Bias, Fade));
        Assert.Equal(from, f);
        Assert.Equal(to, t);
        Assert.Equal(alpha, a, 4);
    }

    [Theory]
    [InlineData(0.0f, 0f)]     // sunrise on the horizon
    [InlineData(0.25f, 90f)]   // solar noon overhead (= bias/2)
    [InlineData(0.5f, 180f)]   // sunset (= bias)
    [InlineData(0.75f, 270f)]  // midnight, sun under the map
    [InlineData(1.0f, 360f)]   // wraps back to dawn
    public void SunPitch_Rotates360OverTheCycle(float time, float pitch)
    {
        Assert.Equal(pitch, LightingCycle.SunPitchDegrees(time, Bias), 3);
    }

    [Fact]
    public void SunPitch_AsymmetricBias_SlowsTheLongerHalf()
    {
        Assert.Equal(90f, LightingCycle.SunPitchDegrees(0.35f, 0.7f), 3);  // noon at bias/2
        Assert.Equal(270f, LightingCycle.SunPitchDegrees(0.85f, 0.7f), 3); // midnight at (1+bias)/2
    }

    private static LightingKeyframe Flat(float v) => new(
        sun: new Color(v, v, v), sea: new Color(v, v, v), fog: new Color(v, v, v),
        skyTop: new Color(v, v, v), skyHorizon: new Color(v, v, v), skyGround: new Color(v, v, v),
        ambientSky: new Color(v, v, v), ambientEquator: new Color(v, v, v), ambientGround: new Color(v, v, v),
        intensity: v, fogDensity: v, clouds: v, shadows: v, rays: v, cloudColor: new Color(v, v, v),
        raysColor: new Color(v, v, v));

    private static readonly IReadOnlyList<LightingKeyframe> Times = new[]
    {
        Flat(0.1f), // dawn
        Flat(0.9f), // midday
        Flat(0.5f), // dusk
        Flat(0.0f), // midnight
    };

    [Fact]
    public void Evaluate_SteadyMidday_ReturnsTheKeyframeUnblended()
    {
        LightingKeyframe k = LightingCycle.Evaluate(Times, 0.25f, Bias, Fade);
        Assert.Equal(0.9f, k.Intensity, 4);
        Assert.Equal(0.9f, k.Sun.R, 4);
    }

    [Fact]
    public void Evaluate_MorningFadeMidpoint_AveragesDawnAndMidday()
    {
        LightingKeyframe k = LightingCycle.Evaluate(Times, 0.0625f, Bias, Fade);
        Assert.Equal(0.5f, k.Intensity, 4);   // (0.1 + 0.9) / 2
        Assert.Equal(0.5f, k.AmbientSky.G, 4);
        Assert.Equal(0.5f, k.FogDensity, 4);
        Assert.Equal(0.5f, k.Shadows, 4);
        Assert.Equal(0.5f, k.CloudColor.R, 4); // ELightingColor.CLOUDS blends like every other channel
        Assert.Equal(0.5f, k.RaysColor.R, 4);  // and so does ELightingColor.RAYS
    }

    // updateLighting's stars ramp (LevelLighting.cs 895/917/933/951): held at 1.0 all day, lerped to 0.05
    // across dusk->midnight, held at 0.05 through midnight, lerped back to 1.0 across midnight->dawn.
    [Theory]
    [InlineData(LightingTime.Dawn, LightingTime.Midday, 0.5f, 1f)]
    [InlineData(LightingTime.Midday, LightingTime.Midday, 0f, 1f)]
    [InlineData(LightingTime.Midday, LightingTime.Dusk, 0.9f, 1f)]
    [InlineData(LightingTime.Dusk, LightingTime.Midnight, 0f, 1f)]
    [InlineData(LightingTime.Dusk, LightingTime.Midnight, 0.5f, 0.525f)]
    [InlineData(LightingTime.Dusk, LightingTime.Midnight, 1f, 0.05f)]
    [InlineData(LightingTime.Midnight, LightingTime.Midnight, 0f, 0.05f)]
    [InlineData(LightingTime.Midnight, LightingTime.Dawn, 0.5f, 0.525f)]
    [InlineData(LightingTime.Midnight, LightingTime.Dawn, 1f, 1f)]
    public void StarsCutoff_RampsAcrossTheNight(LightingTime from, LightingTime to, float alpha, float expected)
    {
        Assert.Equal(expected, LightingCycle.StarsCutoff(from, to, alpha), 4);
    }

    [Fact]
    public void SkyboxSunColor_IsHalfwayToWhite()
    {
        // updateLighting: skybox _SunColor = Color.Lerp(sunLight.color, white, 0.5).
        Color sun = LightingCycle.SkyboxSunColor(new Color(1f, 0f, 0f));
        Assert.Equal(1f, sun.R, 4);
        Assert.Equal(0.5f, sun.G, 4);
        Assert.Equal(0.5f, sun.B, 4);
    }

    // The Lighting.prefab authors MoonLightDirection_0..4 at Unity Y-euler -120, -60, 0, +60, +120; index 2
    // (yaw 0: light along the moon direction) is the full moon.
    [Theory]
    [InlineData(0, -120f)]
    [InlineData(1, -60f)]
    [InlineData(2, 0f)]
    [InlineData(4, 120f)]
    public void MoonPhaseYaw_MatchesTheAuthoredPrefabTransforms(int phase, float yaw)
    {
        Assert.Equal(yaw, LightingCycle.MoonPhaseYawDegrees(phase), 4);
    }

    [Fact]
    public void FullMoon_IsPhaseTwoOfFive()
    {
        Assert.Equal(5, LightingCycle.MoonPhaseCount);
        Assert.Equal(0f, LightingCycle.MoonPhaseYawDegrees(LightingCycle.FullMoonPhase), 4);
    }

    // The defining property of the Exp2->exponential conversion: at the calibration distance, Godot's
    // exponential fog opacity (1 - e^(-D*z)) equals Unity's Exp2 opacity (1 - e^(-(d*z)^2)) for the same
    // authored FOG setting. Uses PEI's real midday FOG density (0.1467) plus a denser synthetic case.
    [Theory]
    [InlineData(0.1467f, 4000f)] // PEI midday, horizon-calibrated
    [InlineData(0.5f, 4000f)]
    [InlineData(0.1467f, 1000f)]
    public void GodotFogDensity_MatchesUnityExp2OpacityAtCalibrationDistance(float fog, float zRef)
    {
        float unityDensity = fog * fog * fog * 0.025f;
        double unityOpacity = 1.0 - System.Math.Exp(-System.Math.Pow(unityDensity * zRef, 2));

        float godotDensity = LightingCycle.GodotFogDensity(fog, zRef);
        double godotOpacity = 1.0 - System.Math.Exp(-(double)godotDensity * zRef);

        Assert.Equal(unityOpacity, godotOpacity, 6);
    }

    // The upper-hemisphere solid-angle average of Unity's trilight: integrating lerp(equator, sky,
    // cos(theta)) over the hemisphere gives (sky + equator) / 2 exactly — verified here against a
    // numerical integration of the trilight itself, so the closed form can't silently drift.
    [Fact]
    public void FlatAmbient_EqualsHemisphereIntegralOfTrilight()
    {
        var sky = new Color(0.8f, 0.627f, 0.388f);      // PEI midday ambient sky
        var equator = new Color(0.722f, 0.62f, 0.467f); // PEI midday ambient equator

        // Numerical solid-angle integral over the upper hemisphere: weight sin(theta), blend cos(theta).
        double num = 0, den = 0;
        for (int i = 0; i < 10000; i++)
        {
            double theta = (i + 0.5) / 10000.0 * (System.Math.PI / 2.0);
            double w = System.Math.Sin(theta);
            num += w * System.Math.Cos(theta);
            den += w;
        }
        float blend = (float)(num / den); // = 0.5 analytically

        Color expected = new(
            equator.R + ((sky.R - equator.R) * blend),
            equator.G + ((sky.G - equator.G) * blend),
            equator.B + ((sky.B - equator.B) * blend));
        Color got = LightingCycle.FlatAmbient(sky, equator);

        Assert.Equal(expected.R, got.R, 3);
        Assert.Equal(expected.G, got.G, 3);
        Assert.Equal(expected.B, got.B, 3);
    }
}

// LightingManager's gameplay clock: the latch that advances the moon at each new night, and the two
// predicates the zombie population reads off it. Ported from LightingManager.updateLighting
// (LightingManager.cs:513-575) and ReceiveInitialLightingState (LightingManager.cs:386-387).
public class DayNightClockTests
{
    private const float ClockBias = 0.6f;

    private static DayNightClock At(float time, int phase = 0) =>
        new(ClockBias, time, phase);

    // One second of a 3600-second cycle, so a test can step the clock in cycle units rather than in
    // guessed wall time.
    private static float Seconds(float cycleFraction) =>
        cycleFraction * LightingCycle.DefaultCycleSeconds;

    [Fact]
    public void ConstructorWrapsTheTimeAndThePhase()
    {
        Assert.Equal(0.25f, new DayNightClock(ClockBias, 1.25f, 0).TimeOfDay, 4);
        Assert.Equal(0.75f, new DayNightClock(ClockBias, -0.25f, 0).TimeOfDay, 4);
        Assert.Equal(2, new DayNightClock(ClockBias, 0f, 7).MoonPhase);      // 7 % 5
        Assert.Equal(3, new DayNightClock(ClockBias, 0f, -2).MoonPhase);     // -2 -> 3
        Assert.Equal(ClockBias, new DayNightClock(ClockBias, 0f, 0).Bias);
    }

    // onLevelLoaded starts a server with isCycled false and lets the first updateLighting latch it, so a
    // clock built at night is NOT cycled until it has been advanced once. That is what makes the phase
    // advance on the server's first frame rather than the saved phase being taken as the live one.
    [Fact]
    public void ANewClockAtNightIsNotYetCycled()
    {
        DayNightClock clock = At(0.9f, LightingCycle.FullMoonPhase);

        Assert.True(clock.IsNighttime);
        Assert.False(clock.IsCycled);
        Assert.False(clock.IsFullMoon);
    }

    // The dusk edge: isCycled latches and the moon advances one slice. Phase 1 -> 2 is the full moon.
    [Fact]
    public void CrossingDuskAdvancesTheMoonOnceAndLatches()
    {
        DayNightClock clock = At(0.59f, 1);
        clock.Advance(Seconds(0.02f));

        Assert.True(clock.IsCycled);
        Assert.Equal(LightingCycle.FullMoonPhase, clock.MoonPhase);
        Assert.True(clock.IsFullMoon);
        Assert.True(clock.IsNighttime);

        // And it is an EDGE: staying in the night does not keep advancing it.
        clock.Advance(Seconds(0.1f));
        Assert.Equal(LightingCycle.FullMoonPhase, clock.MoonPhase);
    }

    // "if (moon < MOON_CYCLES - 1) moon++ else moon = 0" — the wrap at the end of the cycle.
    [Fact]
    public void TheMoonPhaseWrapsAtTheEndOfItsCycle()
    {
        DayNightClock clock = At(0.59f, LightingCycle.MoonPhaseCount - 1);
        clock.Advance(Seconds(0.02f));

        Assert.Equal(0, clock.MoonPhase);
        Assert.False(clock.IsFullMoon);
    }

    // Dawn drops the latch — which is what turns the full moon off — without touching the phase.
    [Fact]
    public void CrossingDawnDropsTheLatchAndTheFullMoon()
    {
        DayNightClock clock = At(0.59f, 1);
        clock.Advance(Seconds(0.02f));
        Assert.True(clock.IsFullMoon);

        clock.Advance(Seconds(0.5f)); // 0.61 -> 0.11: past midnight, into the new day
        Assert.True(clock.IsDaytime);
        Assert.False(clock.IsCycled);
        Assert.False(clock.IsFullMoon);
        Assert.Equal(LightingCycle.FullMoonPhase, clock.MoonPhase); // the phase itself is kept

        // And the next dusk advances it again, so nights keep stepping through the cycle.
        clock.Advance(Seconds(0.5f));
        Assert.Equal(3, clock.MoonPhase);
    }

    // DAY_SPEED. The same wall time covers more of the cycle, and the latch still fires exactly once.
    [Fact]
    public void SpeedScalesHowFarOneAdvanceMoves()
    {
        DayNightClock slow = At(0.1f);
        DayNightClock fast = At(0.1f);
        slow.Advance(Seconds(0.01f));
        fast.Advance(Seconds(0.01f), speed: 10f);

        Assert.Equal(0.11f, slow.TimeOfDay, 4);
        Assert.Equal(0.2f, fast.TimeOfDay, 4);
    }

    // Assigning the time is ReceiveInitialLightingState's shape: re-derive isCycled from where the clock
    // now stands, but do NOT advance the phase — a clock that was jumped has not lived through a dusk.
    [Fact]
    public void AssigningTheTimeReDerivesTheLatchWithoutAdvancingTheMoon()
    {
        DayNightClock clock = At(0.1f, LightingCycle.FullMoonPhase);
        Assert.False(clock.IsFullMoon);

        clock.TimeOfDay = 0.9f;
        Assert.True(clock.IsCycled);
        Assert.True(clock.IsFullMoon);
        Assert.Equal(LightingCycle.FullMoonPhase, clock.MoonPhase); // unchanged

        clock.TimeOfDay = 1.1f; // wraps to 0.1
        Assert.Equal(0.1f, clock.TimeOfDay, 4);
        Assert.False(clock.IsCycled);
        Assert.False(clock.IsFullMoon);
    }

    // Sync alone, for a clock that is pinned rather than assigned (TIME_OF_DAY freezes the cycle, and a
    // frozen clock never runs an edge of its own).
    [Fact]
    public void SyncLatchesAPinnedClockWithoutAdvancingIt()
    {
        DayNightClock clock = At(0.9f);
        clock.SetMoonPhase(LightingCycle.FullMoonPhase);
        Assert.False(clock.IsFullMoon);

        clock.Sync();

        Assert.True(clock.IsCycled);
        Assert.True(clock.IsFullMoon);
        Assert.Equal(0.9f, clock.TimeOfDay, 4);
    }

    [Fact]
    public void SetMoonPhaseWraps()
    {
        DayNightClock clock = At(0.1f);
        clock.SetMoonPhase(LightingCycle.MoonPhaseCount + 2);
        Assert.Equal(2, clock.MoonPhase);
        clock.SetMoonPhase(-1);
        Assert.Equal(LightingCycle.MoonPhaseCount - 1, clock.MoonPhase);
    }

    // Exactly at the bias is neither daytime nor cycled — the original's asymmetry, preserved by the
    // clock because it delegates both predicates to LightingCycle.
    [Fact]
    public void ExactlyAtTheBiasIsNeitherDaytimeNorCycled()
    {
        DayNightClock clock = At(0.1f);
        clock.TimeOfDay = ClockBias;

        Assert.False(clock.IsDaytime);
        Assert.True(clock.IsNighttime);
        Assert.False(clock.IsCycled);
    }
}
