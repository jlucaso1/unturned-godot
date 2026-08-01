using System.Collections.Generic;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class UnityShaderBlendTests
{
    private static Dictionary<string, object> Tags(params (string Key, string Value)[] tags)
    {
        var entries = new List<object>();
        foreach ((string key, string value) in tags)
            entries.Add(new Dictionary<string, object> { ["first"] = key, ["second"] = value });

        return new Dictionary<string, object> { ["tags"] = entries };
    }

    // A serialized Shader with one subshader, optionally carrying its own tags and a pass with tags.
    private static Dictionary<string, object> Shader(Dictionary<string, object>? subShaderTags,
        params Dictionary<string, object>[] passTags)
    {
        var passes = new List<object>();
        foreach (Dictionary<string, object> tags in passTags)
            passes.Add(new Dictionary<string, object> { ["m_Tags"] = tags });

        var subShader = new Dictionary<string, object> { ["m_Passes"] = passes };
        if (subShaderTags != null)
            subShader["m_Tags"] = subShaderTags;

        return new Dictionary<string, object>
        {
            ["m_ParsedForm"] = new Dictionary<string, object>
            {
                ["m_SubShaders"] = new List<object> { subShader },
            },
        };
    }

    [Fact]
    public void Read_AlphaTestQueue_IsCutout()
    {
        // Custom/Foliage, the shader workshop maps hang their vegetation on.
        Dictionary<string, object> shader = Shader(Tags(
            ("IGNOREPROJECTOR", "true"), ("QUEUE", "AlphaTest"), ("RenderType", "TransparentCutout")));

        Assert.Equal(UnityMaterial.Blend.Cutout, UnityShaderBlend.Read(shader));
    }

    [Fact]
    public void Read_TransparentCutout_IsCheckedBeforeTransparent()
    {
        // "TransparentCutout" starts with "Transparent"; the more specific name has to win.
        Assert.Equal(UnityMaterial.Blend.Cutout,
            UnityShaderBlend.Read(Shader(Tags(("RenderType", "TransparentCutout")))));
        Assert.Equal(UnityMaterial.Blend.Alpha,
            UnityShaderBlend.Read(Shader(Tags(("RenderType", "Transparent")))));
    }

    [Fact]
    public void Read_QueueWithAnOffset_StillNamesTheKind()
    {
        Assert.Equal(UnityMaterial.Blend.Cutout,
            UnityShaderBlend.Read(Shader(Tags(("QUEUE", "AlphaTest+50")))));
        Assert.Equal(UnityMaterial.Blend.Alpha,
            UnityShaderBlend.Read(Shader(Tags(("QUEUE", "Transparent-100")))));
    }

    [Fact]
    public void Read_FallsBackToThePassTags()
    {
        // No tags on the subshader itself: the passes carry them (Unturned's own shaders do both).
        Dictionary<string, object> shader = Shader(null,
            Tags(("LIGHTMODE", "FORWARDBASE")),
            Tags(("QUEUE", "AlphaTest")));

        Assert.Equal(UnityMaterial.Blend.Cutout, UnityShaderBlend.Read(shader));
    }

    [Fact]
    public void Read_PassesWithoutTags_AreSkipped()
    {
        // Passes are dictionaries whose m_Tags may be missing entirely; the scan must walk past them.
        Dictionary<string, object> shader = Shader(null,
            new Dictionary<string, object>(),
            Tags(("RenderType", "TransparentCutout")));

        Assert.Equal(UnityMaterial.Blend.Cutout, UnityShaderBlend.Read(shader));
    }

    [Fact]
    public void Read_TagPairMissingItsValue_IsIgnored()
    {
        Dictionary<string, object> onlyValue = new()
        {
            ["tags"] = new List<object>
            {
                new Dictionary<string, object> { ["second"] = "AlphaTest" }, // no key
                new Dictionary<string, object> { ["first"] = "QUEUE", ["second"] = "AlphaTest" },
            },
        };

        Assert.Equal(UnityMaterial.Blend.Cutout, UnityShaderBlend.Read(Shader(onlyValue)));
    }

    [Fact]
    public void Read_OpaqueOrUnknownTags_SayNothing()
    {
        // A shader that declares plain geometry has no opinion to override the material with.
        Assert.Null(UnityShaderBlend.Read(Shader(Tags(("QUEUE", "Geometry"), ("RenderType", "Opaque")))));
        Assert.Null(UnityShaderBlend.Read(Shader(Tags(("SHADOWSUPPORT", "true")))));
        Assert.Null(UnityShaderBlend.Read(Shader(null)));
    }

    [Fact]
    public void Read_MalformedShader_IsNull()
    {
        Assert.Null(UnityShaderBlend.Read(new Dictionary<string, object>()));
        Assert.Null(UnityShaderBlend.Read(new Dictionary<string, object> { ["m_ParsedForm"] = "not a node" }));
        Assert.Null(UnityShaderBlend.Read(new Dictionary<string, object>
        {
            ["m_ParsedForm"] = new Dictionary<string, object> { ["m_SubShaders"] = "not a list" },
        }));
        Assert.Null(UnityShaderBlend.Read(new Dictionary<string, object>
        {
            ["m_ParsedForm"] = new Dictionary<string, object>
            {
                ["m_SubShaders"] = new List<object> { "not a subshader" },
            },
        }));

        // A pass entry that is not a node at all, and a tag pair whose value is null.
        Assert.Null(UnityShaderBlend.Read(new Dictionary<string, object>
        {
            ["m_ParsedForm"] = new Dictionary<string, object>
            {
                ["m_SubShaders"] = new List<object>
                {
                    new Dictionary<string, object> { ["m_Passes"] = new List<object> { "not a pass" } },
                },
            },
        }));
        Assert.Null(UnityShaderBlend.Read(Shader(new Dictionary<string, object>
        {
            ["tags"] = new List<object>
            {
                new Dictionary<string, object> { ["first"] = "QUEUE", ["second"] = null! },
            },
        })));

        // A tag list whose entries are not pairs, and a subshader with no passes key.
        Assert.Null(UnityShaderBlend.Read(Shader(new Dictionary<string, object>
        {
            ["tags"] = new List<object> { "not a pair", new Dictionary<string, object> { ["first"] = "QUEUE" } },
        })));
        Assert.Null(UnityShaderBlend.Read(new Dictionary<string, object>
        {
            ["m_ParsedForm"] = new Dictionary<string, object>
            {
                ["m_SubShaders"] = new List<object> { new Dictionary<string, object>() },
            },
        }));
    }
}
