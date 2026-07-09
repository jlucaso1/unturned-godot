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

    // Unturned sets RenderSettings.fogDensity = FOG^3 * 0.025 (LevelLighting.updateLighting) and leaves
    // Unity on its default Exponential-Squared fog: opacity(z) = 1 - exp(-(d*z)^2). Godot's exponential
    // fog computes opacity(z) = 1 - exp(-D*z) — a different curve, so no single density reproduces Exp2 at
    // every distance. Matching the opacities at a calibration distance gives D = d^2 * zRef exactly:
    // 1 - exp(-D*zRef) = 1 - exp(-(d*zRef)^2)  =>  D*zRef = d^2*zRef^2  =>  D = d^2*zRef.
    // Calibrated at the horizon (the far plane), the haze depth matches Unturned's exactly there; between,
    // Godot's linear curve is slightly denser than Unity's quadratic one — the closest single-parameter fit.
    public static float GodotFogDensity(float fogSetting, float calibrationDistance)
    {
        float unityDensity = fogSetting * fogSetting * fogSetting * 0.025f;
        return unityDensity * unityDensity * calibrationDistance;
    }

    // Unity's trilight ambient lights each surface by its normal: up-facing gets the sky color, horizontal
    // the equator color, down-facing the ground color (a hemisphere blend). Godot's flat ambient has no
    // normal term, so translate by the solid-angle average of the trilight over the UPPER hemisphere —
    // integrating lerp(equator, sky, cos(theta)) over the hemisphere gives exactly (sky + equator) / 2.
    // Up-facing surfaces (terrain, roads — the bulk of every frame) keep the warm sky bounce this way;
    // walls sit half a step off, and rarely-seen down-facing surfaces lose the ground color entirely.
    public static Color FlatAmbient(Color sky, Color equator)
        => new((sky.R + equator.R) / 2f, (sky.G + equator.G) / 2f, (sky.B + equator.B) / 2f);

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
