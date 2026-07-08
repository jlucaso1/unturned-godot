using System.Collections.Generic;
using Godot;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class UnityMaterialTests
{
    private static Dictionary<string, object> ColorEntry(string name, float r, float g, float b, float a) =>
        new()
        {
            ["first"] = name,
            ["second"] = new Dictionary<string, object> { ["r"] = r, ["g"] = g, ["b"] = b, ["a"] = a },
        };

    private static Dictionary<string, object> TexEntry(string name, int fileId, long pathId) =>
        new()
        {
            ["first"] = name,
            ["second"] = new Dictionary<string, object>
            {
                ["m_Texture"] = new Dictionary<string, object> { ["m_FileID"] = fileId, ["m_PathID"] = pathId },
            },
        };

    private static Dictionary<string, object> FloatEntry(string name, float v) =>
        new() { ["first"] = name, ["second"] = v };

    private static Dictionary<string, object> Material(List<object>? colors, List<object>? texEnvs,
        List<object>? floats = null, int? renderQueue = null)
    {
        var saved = new Dictionary<string, object>();
        if (colors != null) saved["m_Colors"] = colors;
        if (texEnvs != null) saved["m_TexEnvs"] = texEnvs;
        if (floats != null) saved["m_Floats"] = floats;
        var mat = new Dictionary<string, object> { ["m_SavedProperties"] = saved };
        if (renderQueue.HasValue) mat["m_CustomRenderQueue"] = renderQueue.Value;
        return mat;
    }

    [Fact]
    public void GetColor_Found()
    {
        var mat = Material(new List<object> { ColorEntry("_Color", 0.5f, 0.4f, 0.3f, 1f) }, null);
        Assert.Equal(new Color(0.5f, 0.4f, 0.3f, 1f), UnityMaterial.GetColor(mat, "_Color"));
    }

    [Fact]
    public void GetColor_MissingProperty_And_MissingSection()
    {
        var mat = Material(new List<object> { ColorEntry("_EmissionColor", 0, 0, 0, 1) }, null);
        Assert.Null(UnityMaterial.GetColor(mat, "_Color"));            // property not present
        Assert.Null(UnityMaterial.GetColor(Material(null, null), "_Color")); // no m_Colors
        Assert.Null(UnityMaterial.GetColor(new Dictionary<string, object>(), "_Color")); // no m_SavedProperties
    }

    [Fact]
    public void GetFloat_FoundAndMissing()
    {
        var mat = Material(null, null, new List<object> { FloatEntry("_Mode", 3f) });
        Assert.Equal(3f, UnityMaterial.GetFloat(mat, "_Mode"));
        Assert.Null(UnityMaterial.GetFloat(mat, "_Cutoff"));
        Assert.Null(UnityMaterial.GetFloat(Material(null, null), "_Mode"));      // no m_Floats
        Assert.Null(UnityMaterial.GetFloat(new Dictionary<string, object>(), "_Mode")); // no props
    }

    [Fact]
    public void IsTransparent_ByRenderQueue()
    {
        Assert.True(UnityMaterial.IsTransparent(Material(null, null, renderQueue: 3000)));
        Assert.False(UnityMaterial.IsTransparent(Material(null, null, renderQueue: 2000)));
    }

    [Theory]
    [InlineData(0f, false)]
    [InlineData(1f, false)]
    [InlineData(2f, true)]  // Fade
    [InlineData(3f, true)]  // Transparent
    public void IsTransparent_ByMode(float mode, bool expected)
    {
        var mat = Material(null, null, new List<object> { FloatEntry("_Mode", mode) });
        Assert.Equal(expected, UnityMaterial.IsTransparent(mat));
    }

    [Fact]
    public void IsTransparent_NoModeNoQueue_IsFalse()
    {
        Assert.False(UnityMaterial.IsTransparent(Material(null, null)));
    }

    [Fact]
    public void GetTexture_Found()
    {
        var mat = Material(null, new List<object> { TexEntry("_MainTex", 0, 555) });
        Assert.Equal((0, 555L), UnityMaterial.GetTexture(mat, "_MainTex"));
    }

    [Fact]
    public void GetTexture_MissingProperty_And_MissingSection()
    {
        var mat = Material(null, new List<object> { TexEntry("_BumpMap", 0, 1) });
        Assert.Equal((0, 0L), UnityMaterial.GetTexture(mat, "_MainTex"));
        Assert.Equal((0, 0L), UnityMaterial.GetTexture(Material(null, null), "_MainTex"));
        Assert.Equal((0, 0L), UnityMaterial.GetTexture(new Dictionary<string, object>(), "_MainTex"));
    }
}
