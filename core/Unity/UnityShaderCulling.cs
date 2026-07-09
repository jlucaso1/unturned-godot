using System.Collections.Generic;

namespace UnturnedGodot.Unity;

// How a material culls, read straight from its serialized Shader's parsed form — the same authored
// "Cull Off/Front/Back" state the game's shaders carry, no name tables on our side. Values follow
// UnityEngine.Rendering.CullMode: 0 = Off (two-sided), 1 = Front, 2 = Back (Unity's default when a
// shader declares no Cull statement — the serializer stores the resolved default explicitly).
public enum EShaderCull : byte
{
    Back = 0,
    TwoSided = 1,
    Front = 2,
}

public static class UnityShaderCulling
{
    // Reads the render culling of a serialized Shader (classId 48) TypeTree dictionary. The state
    // comes from the first FORWARDBASE pass of the first subshader — the pass the engine renders in
    // forward mode — falling back to the first pass with any state. Editor-only passes (the Decalable
    // shaders' META pass authors Cull Off purely for the lightmapper) are naturally skipped by the
    // FORWARDBASE preference. A shader with no readable state culls Back, the engine default.
    public static EShaderCull Read(Dictionary<string, object> shader)
    {
        if (!shader.TryGetValue("m_ParsedForm", out object? pf) || pf is not Dictionary<string, object> parsed
            || !parsed.TryGetValue("m_SubShaders", out object? subs) || subs is not List<object> subShaders
            || subShaders.Count == 0 || subShaders[0] is not Dictionary<string, object> subShader
            || !subShader.TryGetValue("m_Passes", out object? ps) || ps is not List<object> passes)
        {
            return EShaderCull.Back;
        }

        float? firstCull = null;
        foreach (object entry in passes)
        {
            if (entry is not Dictionary<string, object> pass
                || !pass.TryGetValue("m_State", out object? st) || st is not Dictionary<string, object> state
                || !state.TryGetValue("culling", out object? c) || c is not Dictionary<string, object> culling
                || !culling.TryGetValue("val", out object? val))
            {
                continue;
            }

            float cull = System.Convert.ToSingle(val);
            firstCull ??= cull;
            if (LightMode(state) == "FORWARDBASE")
                return FromUnityCullMode(cull);
        }
        return firstCull is { } any ? FromUnityCullMode(any) : EShaderCull.Back;
    }

    private static string? LightMode(Dictionary<string, object> state)
    {
        if (!state.TryGetValue("m_Tags", out object? tg) || tg is not Dictionary<string, object> tags
            || !tags.TryGetValue("tags", out object? tl) || tl is not List<object> tagList)
        {
            return null;
        }
        foreach (object entry in tagList)
        {
            if (entry is Dictionary<string, object> pair
                && pair.TryGetValue("first", out object? k) && (string)k! == "LIGHTMODE"
                && pair.TryGetValue("second", out object? v))
            {
                return (string)v!;
            }
        }
        return null;
    }

    private static EShaderCull FromUnityCullMode(float value) => value switch
    {
        0f => EShaderCull.TwoSided,
        1f => EShaderCull.Front,
        _ => EShaderCull.Back,
    };
}
