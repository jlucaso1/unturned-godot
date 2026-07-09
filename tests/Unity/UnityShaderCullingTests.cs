using System.Collections.Generic;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

// Ground truth is the serialized shader data probed from the real masterbundle: FORWARDBASE passes carry
// culling.val 0 (Framework/Grass, authored Cull Off), 2 (the Standard family, Unity's default), and the
// decal projectors carry 1 (Cull Front) in their single unnamed pass.
public class UnityShaderCullingTests
{
    private static Dictionary<string, object> Pass(float? cull, string? lightMode)
    {
        var state = new Dictionary<string, object>();
        if (cull is { } c)
            state["culling"] = new Dictionary<string, object> { ["val"] = c };
        if (lightMode != null)
            state["m_Tags"] = new Dictionary<string, object>
            {
                ["tags"] = new List<object>
                {
                    new Dictionary<string, object> { ["first"] = "LIGHTMODE", ["second"] = lightMode },
                },
            };
        return new Dictionary<string, object> { ["m_State"] = state };
    }

    private static Dictionary<string, object> Shader(params Dictionary<string, object>[] passes) => new()
    {
        ["m_ParsedForm"] = new Dictionary<string, object>
        {
            ["m_SubShaders"] = new List<object>
            {
                new Dictionary<string, object> { ["m_Passes"] = new List<object>(passes) },
            },
        },
    };

    [Theory]
    [InlineData(0f, EShaderCull.TwoSided)] // Cull Off (Framework/Grass, Custom/Foliage)
    [InlineData(1f, EShaderCull.Front)]    // Cull Front (Decal projectors)
    [InlineData(2f, EShaderCull.Back)]     // Cull Back (Standard family / engine default)
    public void Read_ForwardBasePass_MapsUnityCullModes(float cull, EShaderCull expected)
    {
        Assert.Equal(expected, UnityShaderCulling.Read(Shader(Pass(cull, "FORWARDBASE"))));
    }

    [Fact]
    public void Read_PrefersForwardBase_OverEarlierPasses()
    {
        // The Decalable shaders author Cull Off in their lightmapper-only META pass; the render pass
        // (FORWARDBASE) culls Back and must win regardless of pass order.
        var shader = Shader(Pass(0f, "META"), Pass(2f, "FORWARDBASE"));
        Assert.Equal(EShaderCull.Back, UnityShaderCulling.Read(shader));
    }

    [Fact]
    public void Read_NoForwardBase_UsesTheFirstStatefulPass()
    {
        // Single-pass shaders (decals, particles) have no FORWARDBASE tag at all.
        Assert.Equal(EShaderCull.Front, UnityShaderCulling.Read(Shader(Pass(1f, null))));
        Assert.Equal(EShaderCull.Front, UnityShaderCulling.Read(Shader(Pass(null, null), Pass(1f, null))));
    }

    [Fact]
    public void Read_TagVariations_AreTolerated()
    {
        // Passes tagged with something other than LIGHTMODE, or tagged non-FORWARDBASE, fall through to
        // the first-stateful-pass rule.
        var oddTag = Pass(1f, null);
        ((Dictionary<string, object>)oddTag["m_State"])["m_Tags"] = new Dictionary<string, object>
        {
            ["tags"] = new List<object>
            {
                new Dictionary<string, object> { ["first"] = "QUEUE", ["second"] = "Transparent" },
            },
        };
        Assert.Equal(EShaderCull.Front, UnityShaderCulling.Read(Shader(oddTag)));

        var emptyTags = Pass(0f, null);
        ((Dictionary<string, object>)emptyTags["m_State"])["m_Tags"] =
            new Dictionary<string, object> { ["tags"] = new List<object>() };
        Assert.Equal(EShaderCull.TwoSided, UnityShaderCulling.Read(Shader(emptyTags)));

        var deferredOnly = Pass(2f, "DEFERRED");
        Assert.Equal(EShaderCull.Back, UnityShaderCulling.Read(Shader(deferredOnly)));
    }

    [Fact]
    public void Read_MalformedOrStateless_DefaultsToBack()
    {
        Assert.Equal(EShaderCull.Back, UnityShaderCulling.Read(new Dictionary<string, object>()));
        Assert.Equal(EShaderCull.Back, UnityShaderCulling.Read(
            new Dictionary<string, object> { ["m_ParsedForm"] = new Dictionary<string, object>() }));
        Assert.Equal(EShaderCull.Back, UnityShaderCulling.Read(Shader()));            // no passes
        Assert.Equal(EShaderCull.Back, UnityShaderCulling.Read(Shader(Pass(null, null)))); // no culling value
    }
}
