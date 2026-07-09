using Godot;
using UnturnedGodot.Data;

namespace UnturnedGodot;

// Drives the day/night cycle: owns the sun and the world environment and, every frame, applies the lighting
// state LightingCycle evaluates from the map's real Lighting.dat keyframes — sun rotation/color/intensity,
// trilight-averaged ambient, sky gradient, fog and sea color — exactly as Unturned's LevelLighting.updateLighting
// does. Time advances one full day per hour (LightingManager's cycle) starting from the time of day the map
// was saved at. TIME_OF_DAY=0..1 freezes the clock at a moment (screenshots); DAY_SPEED=N accelerates it.
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
        intensity: 1f, fogDensity: 0.147f, clouds: 0.087f, shadows: 1f, rays: 0.25f);

    private const float DefaultAzimuth = 281.74f;

    // Unturned's exponential-squared fog is far lighter than Godot's per-metre exponential fog reads at
    // the same coefficient; this factor matches the on-screen haze depth to Unturned across the map.
    private const float FogDensityScale = 2f;

    private LevelLighting? _lighting;
    private DirectionalLight3D _sun = null!;
    private ProceduralSkyMaterial _sky = null!;
    private Godot.Environment _env = null!;
    private StandardMaterial3D? _water; // sea plane material, tinted with the blended SEA color
    private float _azimuth = DefaultAzimuth;
    private float _time;
    private float _speed = 1f;
    private bool _frozen;

    public static DayNightController Build(LevelLighting? lighting, StandardMaterial3D? waterMaterial)
    {
        var controller = new DayNightController { Name = "DayNight", _lighting = lighting, _water = waterMaterial };

        controller._sun = new DirectionalLight3D
        {
            Name = "Sun",
            ShadowEnabled = true,
            // 2 cascades over a tight 64 m range (Unturned's own shadow draw distance ballpark): the near
            // split stays dense enough for a crisp player shadow in third person, and beyond ~64 m Unturned
            // shows no shadows either. 4 cascades was ~10% frame time for no visible gain here (#6).
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
            DirectionalShadowMaxDistance = 64f,
        };

        controller._sky = new ProceduralSkyMaterial();
        controller._env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = controller._sky },
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightEnergy = 1.0f,
            FogEnabled = true,
            FogMode = Godot.Environment.FogModeEnum.Exponential,
            FogSkyAffect = 0.3f, // keep the sky mostly readable; fog mainly grounds the terrain
        };

        controller.AddChild(controller._sun);
        controller.AddChild(new WorldEnvironment { Environment = controller._env, Name = "WorldEnvironment" });

        controller._azimuth = lighting?.Azimuth ?? DefaultAzimuth;
        controller._time = lighting?.TimeOfDay ?? 0.25f;

        if (OS.GetEnvironment("TIME_OF_DAY") is { Length: > 0 } fixedTime)
        {
            controller._time = Mathf.PosMod(fixedTime.ToFloat(), 1f);
            controller._frozen = true;
        }
        if (OS.GetEnvironment("DAY_SPEED") is { Length: > 0 } speed)
            controller._speed = speed.ToFloat();

        controller.Apply();
        return controller;
    }

    public override void _Process(double delta)
    {
        if (_frozen || _lighting == null)
            return;
        _time = Mathf.PosMod(_time + ((float)delta * _speed / LightingCycle.DefaultCycleSeconds), 1f);
        Apply();
    }

    private void Apply()
    {
        LightingKeyframe k;
        float pitch;
        if (_lighting != null)
        {
            k = LightingCycle.Evaluate(_lighting.Times, _time, _lighting.Bias, _lighting.Fade);
            pitch = LightingCycle.SunPitchDegrees(_time, _lighting.Bias);
        }
        else
        {
            k = DefaultMidday;
            pitch = 50f; // static fallback sun, matching the old fixed environment
        }

        // Unity Euler(pitch, azimuth, 0) -> Godot: X negates (Unity +X pitches the light down, Godot -X
        // does), and the azimuth negates with the world's Z-mirror. Past 180 the sun is under the horizon
        // (night); Unturned leaves the light on all night — ambient does the lighting, exactly like there.
        _sun.RotationDegrees = new Vector3(-pitch, -_azimuth, 0);
        _sun.LightColor = k.Sun;
        _sun.LightEnergy = k.Intensity;
        _sun.ShadowOpacity = k.Shadows; // ELightingSingle.SHADOWS = Unity's sunLight.shadowStrength

        _sky.SkyTopColor = k.SkyTop;
        _sky.SkyHorizonColor = k.SkyHorizon;
        _sky.GroundHorizonColor = k.SkyHorizon;
        _sky.GroundBottomColor = k.SkyGround;

        // Godot has no trilight ambient, so average Unturned's sky/equator/ground into one flat color.
        _env.AmbientLightColor = new Color(
            (k.AmbientSky.R + k.AmbientEquator.R + k.AmbientGround.R) / 3f,
            (k.AmbientSky.G + k.AmbientEquator.G + k.AmbientGround.G) / 3f,
            (k.AmbientSky.B + k.AmbientEquator.B + k.AmbientGround.B) / 3f);

        // Fog fades the distance into the horizon haze. Unturned: RenderSettings.fogDensity = FOG^3 * 0.025.
        _env.FogLightColor = k.Fog;
        _env.FogDensity = Mathf.Pow(k.FogDensity, 3f) * 0.025f * FogDensityScale;

        if (_water != null)
            _water.AlbedoColor = new Color(k.Sea.R, k.Sea.G, k.Sea.B, _water.AlbedoColor.A);
    }
}
