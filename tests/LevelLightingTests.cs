using System.IO;
using System.Text;
using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

public class LevelLightingTests
{
    // Builds a synthetic Lighting.dat so the parser is exercised without the game installed. Colors are
    // encoded as (keyframe, colorIndex, colorIndex*2) so every value is distinguishable when asserted.
    private static byte[] Build(byte version, bool withKeyframes = true)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(version);
            w.Write(281.74f); // azimuth
            w.Write(0.1f);    // bias
            w.Write(0.2f);    // fade
            w.Write(0.32f);   // time of day
            w.Write((byte)3); // moon
            w.Write(0.5f);    // sea level
            w.Write(0.6f);    // snow level
            if (version > 6) w.Write(true);   // canRain
            if (version > 10) w.Write(false); // canSnow
            if (version >= 8) { w.Write(1f); w.Write(2f); }  // rain freq/dur
            if (version >= 11) { w.Write(3f); w.Write(4f); } // snow freq/dur
            if (withKeyframes && version > 9)
                for (int k = 0; k < 4; k++)
                    WriteKeyframe(w, k);
        }
        return ms.ToArray();
    }

    private static void WriteKeyframe(BinaryWriter w, int k)
    {
        for (int i = 0; i < 12; i++) // 12 ELightingColor entries, RGB bytes
        {
            w.Write((byte)k);
            w.Write((byte)i);
            w.Write((byte)(i * 2));
        }
        w.Write(0.5f + k);         // intensity
        w.Write(0.1f * k);         // fog
        w.Write(0.2f * k);         // clouds
        w.Write(0.75f - 0.25f * k); // shadows (kept to exact binary fractions)
        w.Write(0.25f);            // rays
    }

    [Fact]
    public void Parse_ReadsHeaderAndMiddayKeyframe()
    {
        LevelLighting lighting = LevelLighting.Parse(new MemoryStream(Build(12)));

        Assert.Equal(12, lighting.Version);
        Assert.Equal(281.74f, lighting.Azimuth);
        Assert.Equal(0.1f, lighting.Bias);
        Assert.Equal(0.2f, lighting.Fade);
        Assert.Equal(0.32f, lighting.TimeOfDay);
        Assert.Equal(0.5f, lighting.SeaLevel);
        Assert.Equal(4, lighting.Times.Count);

        // Midday is keyframe index 1 (not 0), so its encoded keyframe byte is 1.
        LightingKeyframe day = lighting.Midday;
        Assert.Equal(1f / 255f, day.Sun.R);          // color 0 (SUN), keyframe 1
        Assert.Equal(0f, day.Sun.G);                 // color index 0 -> green byte 0
        Assert.Equal(new Color(1f / 255f, 1f / 255f, 2f / 255f), day.Sea);          // color 1
        Assert.Equal(new Color(1f / 255f, 2f / 255f, 4f / 255f), day.Fog);          // color 2
        Assert.Equal(new Color(1f / 255f, 3f / 255f, 6f / 255f), day.SkyTop);       // color 3
        Assert.Equal(new Color(1f / 255f, 4f / 255f, 8f / 255f), day.SkyHorizon);   // color 4
        Assert.Equal(new Color(1f / 255f, 5f / 255f, 10f / 255f), day.SkyGround);   // color 5
        Assert.Equal(new Color(1f / 255f, 6f / 255f, 12f / 255f), day.AmbientSky);  // color 6
        Assert.Equal(new Color(1f / 255f, 7f / 255f, 14f / 255f), day.AmbientEquator); // color 7
        Assert.Equal(new Color(1f / 255f, 8f / 255f, 16f / 255f), day.AmbientGround);  // color 8
        Assert.Equal(1.5f, day.Intensity);
        Assert.Equal(0.1f, day.FogDensity);
        Assert.Equal(0.2f, day.Clouds);
        Assert.Equal(0.5f, day.Shadows);
        Assert.Equal(0.25f, day.Rays);
    }

    [Fact]
    public void Parse_KeyframesAreOrdered()
    {
        LevelLighting lighting = LevelLighting.Parse(new MemoryStream(Build(12)));
        // Each keyframe's SUN red channel encodes its index, confirming Dawn/Midday/Dusk/Midnight order.
        Assert.Equal(0f, lighting.Times[(int)LightingTime.Dawn].Sun.R);
        Assert.Equal(1f / 255f, lighting.Times[(int)LightingTime.Midday].Sun.R);
        Assert.Equal(2f / 255f, lighting.Times[(int)LightingTime.Dusk].Sun.R);
        Assert.Equal(3f / 255f, lighting.Times[(int)LightingTime.Midnight].Sun.R);
    }

    [Fact]
    public void Parse_MinimalHeaderVersion_StillReadsKeyframes()
    {
        // Version 10 omits the snow freq/dur fields (>= 11) but still uses the modern keyframe layout.
        LevelLighting lighting = LevelLighting.Parse(new MemoryStream(Build(10)));
        Assert.Equal(10, lighting.Version);
        Assert.Equal(1.5f, lighting.Midday.Intensity);
    }

    [Fact]
    public void Parse_VersionTooOld_Throws()
    {
        Assert.Throws<System.NotSupportedException>(
            () => LevelLighting.Parse(new MemoryStream(new byte[] { 4 })));
    }

    [Fact]
    public void Parse_UnsupportedKeyframeVersion_Throws()
    {
        // A version-5 header parses, then the obsolete keyframe encoding (<= 9) is rejected.
        Assert.Throws<System.NotSupportedException>(
            () => LevelLighting.Parse(new MemoryStream(Build(5, withKeyframes: false))));
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        Assert.Null(LevelLighting.Load(Path.Combine(Path.GetTempPath(), "no-such-lighting.dat")));
    }

    [Fact]
    public void Load_ReadsFromDisk()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lighting-{System.Guid.NewGuid():N}.dat");
        File.WriteAllBytes(path, Build(12));
        try
        {
            LevelLighting? lighting = LevelLighting.Load(path);
            Assert.NotNull(lighting);
            Assert.Equal(12, lighting!.Version);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
