using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Godot;

namespace UnturnedGodot.Data;

// The four time-of-day keyframes Unturned interpolates between (LevelLighting.ELightingTime order).
public enum LightingTime
{
    Dawn = 0,
    Midday = 1,
    Dusk = 2,
    Midnight = 3,
}

// One lighting keyframe: the colors and scalars Unturned stores for a time of day. Field order mirrors
// LevelLighting's ELightingColor / ELightingSingle enums; only the values the renderer needs are kept.
public readonly struct LightingKeyframe
{
    public readonly Color Sun;
    public readonly Color Sea;
    public readonly Color Fog;
    public readonly Color SkyTop;     // SKY_SKY: zenith
    public readonly Color SkyHorizon; // SKY_EQUATOR: horizon haze
    public readonly Color SkyGround;  // SKY_GROUND: below the horizon
    public readonly Color AmbientSky;
    public readonly Color AmbientEquator;
    public readonly Color AmbientGround;
    public readonly Color CloudColor; // ELightingColor.CLOUDS: the skybox's cloud rim/body tint
    // ELightingColor.RAYS: the tint of the sun shafts. LevelLighting pairs it with the RAYS single
    // (Rays below) as UpdateSunShafts(sunFlare, raysColor) / raysIntensity = singles[RAYS] * 4.
    public readonly Color RaysColor;
    public readonly float Intensity; // sun brightness
    public readonly float FogDensity;
    public readonly float Clouds;
    public readonly float Shadows; // shadow strength
    public readonly float Rays;

    public LightingKeyframe(Color sun, Color sea, Color fog, Color skyTop, Color skyHorizon, Color skyGround,
        Color ambientSky, Color ambientEquator, Color ambientGround, float intensity, float fogDensity,
        float clouds, float shadows, float rays, Color cloudColor = default, Color raysColor = default)
    {
        Sun = sun;
        Sea = sea;
        Fog = fog;
        SkyTop = skyTop;
        SkyHorizon = skyHorizon;
        SkyGround = skyGround;
        AmbientSky = ambientSky;
        AmbientEquator = ambientEquator;
        AmbientGround = ambientGround;
        CloudColor = cloudColor;
        RaysColor = raysColor;
        Intensity = intensity;
        FogDensity = fogDensity;
        Clouds = clouds;
        Shadows = shadows;
        Rays = rays;
    }
}

// Ports Unturned's per-map lighting file (Environment/Lighting.dat, read by LevelLighting.load). It holds
// the day/night keyframes (dawn/midday/dusk/midnight) plus the sun azimuth and saved time of day, so the
// scene can drive its sun and ambient from real map data instead of hardcoded values. Little-endian;
// colors are three 8-bit RGB channels. Handles the current on-disk layout (header version >= 5, keyframe
// version > 9); the long-obsolete older keyframe encodings are rejected rather than silently mis-read.
public sealed class LevelLighting
{
    private const int ColorsPerKeyframe = 12; // ELightingColor 0..11 (PARTICLE_LIGHTING is the last)
    private const int TimeCount = 4;

    public byte Version { get; }
    public float Azimuth { get; }     // sun compass angle in degrees
    public float Bias { get; }        // fraction of the cycle that is daytime (time < bias = day)
    public float Fade { get; }        // scales the dawn/dusk transition width (see LightingCycle.Transition)
    public float TimeOfDay { get; }   // 0..1 fraction of the day the map was saved at
    public float SeaLevel { get; }    // fraction of Level.TERRAIN (256): water surface Y = SeaLevel * 256
    public byte MoonPhase { get; }    // saved LevelLighting.moon index; advances each night, 2 = full moon
    public IReadOnlyList<LightingKeyframe> Times { get; }

    public LightingKeyframe Midday => Times[(int)LightingTime.Midday];

    private LevelLighting(byte version, float azimuth, float bias, float fade, float timeOfDay, float seaLevel,
        LightingKeyframe[] times, byte moonPhase)
    {
        Version = version;
        Azimuth = azimuth;
        Bias = bias;
        Fade = fade;
        TimeOfDay = timeOfDay;
        SeaLevel = seaLevel;
        MoonPhase = moonPhase;
        Times = times;
    }

    // Loads a map's Lighting.dat, or null when the file is absent (callers fall back to defaults).
    public static LevelLighting? Load(string path)
    {
        if (!File.Exists(path))
            return null;
        using FileStream stream = File.OpenRead(path);
        return Parse(stream);
    }

    public static LevelLighting Parse(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        byte version = r.ReadByte();
        if (version < 5)
            throw new NotSupportedException($"Lighting.dat version {version} is too old to read");

        float azimuth = r.ReadSingle();
        float bias = r.ReadSingle();
        float fade = r.ReadSingle();
        float timeOfDay = r.ReadSingle();
        byte moonPhase = r.ReadByte(); // saved LevelLighting.moon index (0..MOON_CYCLES-1)
        float seaLevel = r.ReadSingle();
        r.ReadSingle();               // snow level (unused)
        if (version > 6)
            r.ReadBoolean();          // canRain
        if (version > 10)
            r.ReadBoolean();          // canSnow
        if (version >= 8)
        {
            r.ReadSingle();           // rain frequency
            r.ReadSingle();           // rain duration
        }
        if (version >= 11)
        {
            r.ReadSingle();           // snow frequency
            r.ReadSingle();           // snow duration
        }

        if (version <= 9)
            throw new NotSupportedException($"Lighting.dat keyframe version {version} is not supported");

        var times = new LightingKeyframe[TimeCount];
        for (int i = 0; i < TimeCount; i++)
            times[i] = ReadKeyframe(r, version);

        return new LevelLighting(version, azimuth, bias, fade, timeOfDay, seaLevel, times, moonPhase);
    }

    // Fog written before the file format reached 12 means something else than fog written after, and
    // LevelLighting.cs:1546 says why in one line: "Switched from height fog to distance fog, so we
    // reduce the intensity on all maps." A height-fog density read as a distance-fog density is far too
    // thick — up to three times, since the clamp caps values that go to 1.0 — so the game ceilings every
    // pre-12 keyframe and this port has to as well. Every official map is version 12 and untouched by
    // it; a workshop map saved before that update is what this is for.
    private const byte DistanceFogVersion = 12;
    private const float LegacyMaxFog = 0.33f;

    // A keyframe is the 12 ELightingColor entries (RGB) followed by the 5 ELightingSingle scalars.
    private static LightingKeyframe ReadKeyframe(BinaryReader r, byte version)
    {
        var colors = new Color[ColorsPerKeyframe];
        for (int i = 0; i < ColorsPerKeyframe; i++)
            colors[i] = ReadColor(r);

        float intensity = r.ReadSingle();
        float fogDensity = r.ReadSingle();
        float clouds = r.ReadSingle();
        float shadows = r.ReadSingle();
        float rays = r.ReadSingle();

        // ELightingSingle.FOG, which is this scalar and not the FOG colour above.
        if (version < DistanceFogVersion)
            fogDensity = Math.Min(fogDensity, LegacyMaxFog);

        return new LightingKeyframe(
            sun: colors[0], sea: colors[1], fog: colors[2],
            skyTop: colors[3], skyHorizon: colors[4], skyGround: colors[5],
            ambientSky: colors[6], ambientEquator: colors[7], ambientGround: colors[8],
            intensity, fogDensity, clouds, shadows, rays,
            cloudColor: colors[9],
            raysColor: colors[10]); // colors[11] (PARTICLE_LIGHTING) is unused by the renderer
    }

    private static Color ReadColor(BinaryReader r)
    {
        float red = r.ReadByte() / 255f;
        float green = r.ReadByte() / 255f;
        float blue = r.ReadByte() / 255f;
        return new Color(red, green, blue);
    }
}
