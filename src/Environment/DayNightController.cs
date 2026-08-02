using Godot;
using UnturnedGodot.Data;

namespace UnturnedGodot;

// Drives the day/night cycle: owns the sun and the world environment and, every frame, applies the lighting
// state LightingCycle evaluates from the map's real Lighting.dat keyframes — sun rotation/color/intensity,
// trilight-averaged ambient, the ported Unturned skybox (gradient, sun disc, stars, moon phases, clouds),
// fog and sea color — exactly as Unturned's LevelLighting.updateLighting does. Time advances one full day
// per hour (LightingManager's cycle) starting from the time of day the map was saved at. TIME_OF_DAY=0..1
// freezes the clock at a moment (screenshots); DAY_SPEED=N accelerates it.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class DayNightController : Node
{
    // PEI's real midday values, used as a static fallback when a map ships no Lighting.dat.
    private static readonly LightingKeyframe DefaultMidday = new(
        sun: new Color(0.933f, 0.863f, 0.757f),
        sea: new Color(0.482f, 0.608f, 0.792f),
        fog: new Color(0.784f, 0.784f, 0.784f),
        skyTop: new Color(0.4f, 0.627f, 0.808f),
        skyHorizon: new Color(0.784f, 0.784f, 0.784f),
        skyGround: new Color(0.329f, 0.518f, 0.78f),
        ambientSky: new Color(0.8f, 0.627f, 0.388f),
        ambientEquator: new Color(0.722f, 0.62f, 0.467f),
        ambientGround: new Color(0.682f, 0.627f, 0.545f),
        intensity: 1f, fogDensity: 0.147f, clouds: 0.087f, shadows: 1f, rays: 0.25f,
        cloudColor: new Color(0.686f, 0.686f, 0.686f));

    private const float DefaultAzimuth = 281.74f;

    // Where the Exp2->exponential fog conversion is opacity-matched (LightingCycle.GodotFogDensity): the
    // default camera far plane, i.e. the horizon — the distance where haze depth is most visible.
    private const float FogCalibrationDistance = 4000f;

    private LevelLighting? _lighting;
    private DirectionalLight3D _sun = null!;
    private ShaderMaterial _sky = null!;
    private Godot.Environment _env = null!;
    private SunShafts? _shafts;

    // LevelLighting: raysIntensity = singles[ELightingSingle.RAYS] * 4.
    private const float RaysIntensityScale = 4f;

    // The sun-shaft pass needs the camera it renders in front of; the caller hands it over once the
    // player (or the free camera) exists. No-op when the effect is switched off.
    public void AttachCamera(Camera3D camera) => _shafts?.Follow(camera);
    private StandardMaterial3D? _water; // sea plane material, tinted with the blended SEA color
    private float _azimuth = DefaultAzimuth;
    private float _time;
    private float _speed = 1f;
    private bool _frozen;
    private int _moonPhase;
    private bool _moonCycled; // this night's phase advance already happened (LightingManager.isCycled)

    public static DayNightController Build(LevelLighting? lighting, StandardMaterial3D? waterMaterial,
        SkyboxAssets? skyAssets = null)
    {
        var controller = new DayNightController { Name = "DayNight", _lighting = lighting, _water = waterMaterial };

        controller._sun = new DirectionalLight3D
        {
            Name = "Sun",
            ShadowEnabled = true,
            // 2 cascades over a tight range, which is where Unturned itself stops drawing shadows. The
            // shadow pass re-rasterizes every caster inside this distance, so it is the single largest
            // block of geometry the frame submits — larger than the objects it shadows.
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
            DirectionalShadowMaxDistance = 64f,
            // Split kept at 16 m of that range: thin casters (fence wires, goal nets) hold their shadow
            // well past walking distance instead of popping in at ~6 m, while the near texel density stays
            // ample for the player's own shadow. Halving the range at the same split means the far cascade
            // now covers half the ground with the same texels, so 16-64 m gets sharper rather than worse.
            DirectionalShadowSplit1 = 0.25f,
            DirectionalShadowBlendSplits = true,
        };

        controller._sky = UnturnedSky.Build(skyAssets);
        controller._env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = controller._sky },
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightEnergy = 1.0f,
            // Specular reflections come from the sky radiance map — Unturned's counterpart is its custom
            // skybox reflection probe (LevelLighting.updateSkyboxReflections). Only materials whose data
            // carries _Metallic/_Glossiness have a specular path, so matte objects are unaffected, and the
            // map is already generated for the sky background: no added cost. The probe naturally dims at
            // night as the sky darkens, standing in for Unturned's animated reflectionIntensity.
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
            FogEnabled = true,
            FogMode = Godot.Environment.FogModeEnum.Exponential,
            // Unturned's clear-weather sky is NOT fogged: RenderSettings.fog only greys the geometry, and
            // the skybox blends toward the fog color only under weather (_AtmosphericFog, 0 when clear).
            FogSkyAffect = 0f,
        };

        controller.AddChild(controller._sun);

        // Off by default, exactly like the game (GraphicsSettingsData.SunShaftsQuality = OFF).
        if (OS.GetEnvironment("SUN_SHAFTS") == "1")
        {
            controller._shafts = SunShafts.Create(controller._sun);
            controller.AddChild(controller._shafts);
        }
        controller.AddChild(new WorldEnvironment { Environment = controller._env, Name = "WorldEnvironment" });

        controller._azimuth = lighting?.Azimuth ?? DefaultAzimuth;
        controller._time = lighting?.TimeOfDay ?? 0.25f;
        controller._moonPhase = lighting?.MoonPhase ?? 0;

        if (OS.GetEnvironment("TIME_OF_DAY") is { Length: > 0 } fixedTime)
        {
            controller._time = Mathf.PosMod(fixedTime.ToFloat(), 1f);
            controller._frozen = true;
        }
        if (OS.GetEnvironment("MOON_PHASE") is { Length: > 0 } phase)
            controller._moonPhase = Mathf.PosMod(phase.ToInt(), LightingCycle.MoonPhaseCount);
        if (OS.GetEnvironment("DAY_SPEED") is { Length: > 0 } speed)
            controller._speed = speed.ToFloat();

        controller.Apply();
        return controller;
    }

    // Re-apply the lighting only after the clock has moved this far through the cycle: 1/7200 is half a
    // second of cycle time (the sun sweeps ~0.05°) — visually indistinguishable from per-frame updates.
    // Applying every frame did ~15 marshaled property writes and dirtied the sky material (a sky +
    // radiance-map redraw) hundreds of times per second for a color delta the eye can't see. The threshold
    // lives in cycle space, so a cranked DAY_SPEED automatically re-applies at the same *visual* cadence.
    private const float ApplyStep = 1f / 7200f;
    private float _appliedTime = float.NaN;

    public override void _Process(double delta)
    {
        if (_frozen || _lighting == null)
            return;
        _time = Mathf.PosMod(_time + ((float)delta * _speed / LightingCycle.DefaultCycleSeconds), 1f);

        // LightingManager advances the moon phase once at each new night (wrapping at MOON_CYCLES), so
        // every night shows the next slice of the moon; full moon is phase 2.
        if (_time >= _lighting.Bias)
        {
            if (!_moonCycled)
            {
                _moonCycled = true;
                _moonPhase = (_moonPhase + 1) % LightingCycle.MoonPhaseCount;
            }
        }
        else
        {
            _moonCycled = false;
        }

        float sinceApplied = Mathf.Abs(Mathf.PosMod(_time - _appliedTime + 0.5f, 1f) - 0.5f); // circular
        if (float.IsNaN(_appliedTime) || sinceApplied >= ApplyStep)
            Apply();
    }

    private void Apply()
    {
        _appliedTime = _time;
        LightingKeyframe k;
        float pitch;
        float starsCutoff;
        if (_lighting != null)
        {
            float transition = LightingCycle.Transition(_lighting.Bias, _lighting.Fade);
            (LightingTime from, LightingTime to, float alpha) = LightingCycle.Blend(_time, _lighting.Bias, transition);
            k = LightingCycle.Evaluate(_lighting.Times, _time, _lighting.Bias, _lighting.Fade);
            pitch = LightingCycle.SunPitchDegrees(_time, _lighting.Bias);
            starsCutoff = LightingCycle.StarsCutoff(from, to, alpha);
        }
        else
        {
            k = DefaultMidday;
            pitch = 50f; // static fallback sun, matching the old fixed environment
            starsCutoff = LightingCycle.StarsCutoffDay;
        }

        // Unity Euler(pitch, azimuth, 0) -> Godot: X negates (Unity +X pitches the light down, Godot -X
        // does), and the azimuth negates with the world's Z-mirror. Past 180 the sun is under the horizon
        // (night); Unturned leaves the light on all night — ambient does the lighting, exactly like there.
        _sun.RotationDegrees = new Vector3(-pitch, -_azimuth, 0);
        _sun.LightColor = k.Sun;
        _sun.LightEnergy = k.Intensity;
        _sun.ShadowOpacity = k.Shadows; // ELightingSingle.SHADOWS = Unity's sunLight.shadowStrength

        // LevelLighting.UpdateSunShafts: the map's RAYS colour, its RAYS single scaled by 4, and the
        // UpdateSunShaftsIntensity(1 - fog) fade — the shafts wash out as the air thickens.
        _shafts?.Configure(k.RaysColor, k.Rays * RaysIntensityScale, 1f - k.FogDensity);

        // The skybox uniforms, exactly what updateLighting pushes each frame. Unity's sun.forward is the
        // light's travel direction: -Z of our (already mirrored) sun basis. The moon sits opposite the sun,
        // and its phase light is the MoonLightDirection_N child: sunRotation * localY(yaw), with the yaw
        // negated by the Z-mirror like every rotation here.
        Vector3 sunForward = -_sun.Basis.Z;
        float moonYaw = Mathf.DegToRad(-LightingCycle.MoonPhaseYawDegrees(_moonPhase));
        Vector3 moonLightForward = _sun.Basis * new Basis(Vector3.Up, moonYaw) * new Vector3(0, 0, -1);
        _sky.SetShaderParameter("sky_color", k.SkyTop);
        _sky.SetShaderParameter("equator_color", k.SkyHorizon);
        _sky.SetShaderParameter("ground_color", k.SkyGround);
        _sky.SetShaderParameter("ambient_equator", k.AmbientEquator);
        _sky.SetShaderParameter("ambient_ground", k.AmbientGround);
        _sky.SetShaderParameter("sun_direction", sunForward);
        _sky.SetShaderParameter("sun_color", LightingCycle.SkyboxSunColor(k.Sun));
        _sky.SetShaderParameter("stars_cutoff", starsCutoff);
        _sky.SetShaderParameter("moon_direction", -sunForward);
        _sky.SetShaderParameter("moon_light_direction", moonLightForward);
        _sky.SetShaderParameter("cloud_rim_color", k.CloudColor);
        _sky.SetShaderParameter("cloud_intensity", k.Clouds);
        _sky.SetShaderParameter("fog_color", k.Fog);

        // Godot has no trilight ambient; use the upper-hemisphere solid-angle average of Unturned's
        // trilight (see LightingCycle.FlatAmbient) so up-facing surfaces keep the warm sky bounce.
        _env.AmbientLightColor = LightingCycle.FlatAmbient(k.AmbientSky, k.AmbientEquator);

        // Fog fades the distance into the horizon haze, opacity-matched to Unturned's Exp2 fog at the
        // calibration distance (see LightingCycle.GodotFogDensity for the derivation).
        _env.FogLightColor = k.Fog;
        _env.FogDensity = LightingCycle.GodotFogDensity(k.FogDensity, FogCalibrationDistance);

        if (_water != null)
            _water.AlbedoColor = new Color(k.Sea.R, k.Sea.G, k.Sea.B, _water.AlbedoColor.A);
    }
}
