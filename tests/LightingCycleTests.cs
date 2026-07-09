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
        intensity: v, fogDensity: v, clouds: v, shadows: v, rays: v);

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
    }
}
