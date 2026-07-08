using Godot;
using UnturnedGodot.Data;

namespace UnturnedGodot;

// Builds the scene's sun and sky/ambient from the map's Lighting.dat, replicating Unturned's lighting
// model (LevelLighting.cs): one directional sun plus a flat ambient. This is the single place map
// atmosphere (ambient, sky, fog) is configured, so it can grow as those features are ported.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class SceneEnvironment
{
    // PEI's real midday values, used as a fallback when a map ships no Lighting.dat.
    private static readonly LightingKeyframe DefaultMidday = new(
        sun: new Color(0.933f, 0.863f, 0.757f),
        sea: new Color(0.482f, 0.608f, 0.792f),
        fog: new Color(0.784f, 0.784f, 0.784f),
        ambientSky: new Color(0.8f, 0.627f, 0.388f),
        ambientEquator: new Color(0.722f, 0.62f, 0.467f),
        ambientGround: new Color(0.682f, 0.627f, 0.545f),
        intensity: 1f, fogDensity: 0.147f, clouds: 0.087f, shadows: 1f, rays: 0.25f);

    private const float DefaultAzimuth = 281.74f;

    public static (DirectionalLight3D Sun, WorldEnvironment World) Build(LevelLighting? lighting)
    {
        LightingKeyframe day = lighting?.Midday ?? DefaultMidday;
        float azimuth = lighting?.Azimuth ?? DefaultAzimuth;

        var sun = new DirectionalLight3D
        {
            Name = "Sun",
            RotationDegrees = new Vector3(-50, -azimuth, 0),
            LightColor = day.Sun,
            LightEnergy = day.Intensity,
            ShadowEnabled = true,
            // 4 cascades is overkill for the ~100 m default shadow range; 2 is visually identical here
            // (shadows are subtle in Unturned's ambient-filled look) and ~10% cheaper on frame time (#6).
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
        };

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial() },
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            // Godot has no trilight ambient, so average Unturned's sky/equator/ground into one flat color.
            AmbientLightColor = AverageAmbient(day),
            AmbientLightEnergy = 1.0f,
        };

        return (sun, new WorldEnvironment { Environment = env, Name = "WorldEnvironment" });
    }

    private static Color AverageAmbient(LightingKeyframe day) => new(
        (day.AmbientSky.R + day.AmbientEquator.R + day.AmbientGround.R) / 3f,
        (day.AmbientSky.G + day.AmbientEquator.G + day.AmbientGround.G) / 3f,
        (day.AmbientSky.B + day.AmbientEquator.B + day.AmbientGround.B) / 3f);
}
