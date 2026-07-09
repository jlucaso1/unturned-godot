using Godot;

namespace UnturnedGodot.Data;

// The day/night cycle maths, a 1:1 port of Unturned's LevelLighting.GetLightingIndices + updateLighting.
// time is the 0..1 fraction of the cycle; bias splits it into day (time < bias) and night; around each of
// the four anchors (dawn = 0, midday, dusk = bias, midnight) the keyframes crossfade over a transition
// window derived from fade. The sun rotates 0..180 degrees (Unity Euler X) across the day and 180..360
// below the horizon across the night, at the map's fixed azimuth.
public static class LightingCycle
{
    public const float DefaultCycleSeconds = 3600f; // LightingManager: one full day per hour

    // LevelLighting.bias/fade setters: the crossfade half-window, bounded by the shorter of day/night.
    public static float Transition(float bias, float fade)
        => (bias < 1f - bias ? bias / 2f : (1f - bias) / 2f) * fade;

    // LevelLighting.GetLightingIndices: which two keyframes are active and the blend between them.
    // blendAlpha = 0 means "steady on To" (the SDK's blendLightingIndex == -1 case).
    public static (LightingTime From, LightingTime To, float Alpha) Blend(float time, float bias, float transition)
    {
        if (time < bias) // daytime
        {
            if (time < transition) // dawn -> midday
                return (LightingTime.Dawn, LightingTime.Midday, time / transition);
            if (time < bias - transition) // steady midday
                return (LightingTime.Midday, LightingTime.Midday, 0f);
            return (LightingTime.Midday, LightingTime.Dusk, (time - bias + transition) / transition);
        }
        if (time < bias + transition) // dusk -> midnight
            return (LightingTime.Dusk, LightingTime.Midnight, (time - bias) / transition);
        if (time < 1f - transition) // steady midnight
            return (LightingTime.Midnight, LightingTime.Midnight, 0f);
        return (LightingTime.Midnight, LightingTime.Dawn, (time - 1f + transition) / transition);
    }

    // The sun's Unity Euler X pitch: 0 at dawn, 90 overhead at solar noon, 180 at dusk, then it keeps
    // rotating 180..360 under the horizon through the night (updateLighting's two sun.rotation branches).
    public static float SunPitchDegrees(float time, float bias)
        => time < bias
            ? time / bias * 180f
            : 180f + ((time - bias) / (1f - bias) * 180f);

    // The fully blended lighting state at a moment of the cycle (updateLighting's Color.Lerp block).
    public static LightingKeyframe Evaluate(System.Collections.Generic.IReadOnlyList<LightingKeyframe> times,
        float time, float bias, float fade)
    {
        (LightingTime from, LightingTime to, float alpha) = Blend(time, bias, Transition(bias, fade));
        LightingKeyframe a = times[(int)from];
        LightingKeyframe b = times[(int)to];
        if (alpha <= 0f)
            return b;

        return new LightingKeyframe(
            sun: a.Sun.Lerp(b.Sun, alpha),
            sea: a.Sea.Lerp(b.Sea, alpha),
            fog: a.Fog.Lerp(b.Fog, alpha),
            skyTop: a.SkyTop.Lerp(b.SkyTop, alpha),
            skyHorizon: a.SkyHorizon.Lerp(b.SkyHorizon, alpha),
            skyGround: a.SkyGround.Lerp(b.SkyGround, alpha),
            ambientSky: a.AmbientSky.Lerp(b.AmbientSky, alpha),
            ambientEquator: a.AmbientEquator.Lerp(b.AmbientEquator, alpha),
            ambientGround: a.AmbientGround.Lerp(b.AmbientGround, alpha),
            intensity: Mathf.Lerp(a.Intensity, b.Intensity, alpha),
            fogDensity: Mathf.Lerp(a.FogDensity, b.FogDensity, alpha),
            clouds: Mathf.Lerp(a.Clouds, b.Clouds, alpha),
            shadows: Mathf.Lerp(a.Shadows, b.Shadows, alpha),
            rays: Mathf.Lerp(a.Rays, b.Rays, alpha));
    }
}
