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
