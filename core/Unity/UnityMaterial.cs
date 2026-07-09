using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Unity;

// Reads texture/color references off a Material's saved properties. Unturned's blocky objects are
// mostly flat-colored via m_Colors["_Color"], with textures (m_TexEnvs["_MainTex"]) on some props.
public static class UnityMaterial
{
    // The color bound to a property (e.g. "_Color"), or null when absent.
    public static Color? GetColor(Dictionary<string, object> material, string property)
    {
        if (!material.TryGetValue("m_SavedProperties", out object? sp) ||
            sp is not Dictionary<string, object> saved ||
            !saved.TryGetValue("m_Colors", out object? mc) ||
            mc is not List<object> colors)
        {
            return null;
        }

        foreach (object entry in colors)
        {
            var pair = (Dictionary<string, object>)entry;
            if ((string)pair["first"] != property)
                continue;
            var c = (Dictionary<string, object>)pair["second"];
            return new Color(ToFloat(c["r"]), ToFloat(c["g"]), ToFloat(c["b"]), ToFloat(c["a"]));
        }
        return null;
    }

    private static float ToFloat(object value) => System.Convert.ToSingle(value);

    // The float bound to a property (e.g. "_Mode"), or null when absent.
    public static float? GetFloat(Dictionary<string, object> material, string property)
    {
        if (!material.TryGetValue("m_SavedProperties", out object? sp) ||
            sp is not Dictionary<string, object> saved ||
            !saved.TryGetValue("m_Floats", out object? mf) ||
            mf is not List<object> floats)
        {
            return null;
        }

        foreach (object entry in floats)
        {
            var pair = (Dictionary<string, object>)entry;
            if ((string)pair["first"] == property)
                return ToFloat(pair["second"]);
        }
        return null;
    }

    // How a material blends. Unity's Standard shader _Mode: 0 Opaque, 1 Cutout (alpha clip, e.g. the
    // Christmas-light garland), 2 Fade / 3 Transparent (alpha blend, e.g. glass). Render queue is the
    // fallback: 2450 == AlphaTest (cutout), >= 3000 == Transparent (blend).
    public enum Blend { Opaque, Cutout, Alpha }

    public static Blend GetBlendMode(Dictionary<string, object> material)
    {
        float? mode = GetFloat(material, "_Mode");
        if (mode == 1f)
            return Blend.Cutout;
        if (mode == 2f || mode == 3f)
            return Blend.Alpha;

        int renderQueue = material.TryGetValue("m_CustomRenderQueue", out object? rq)
            ? System.Convert.ToInt32(rq)
            : -1;
        if (renderQueue >= 3000)
            return Blend.Alpha;
        if (renderQueue >= 2450)
            return Blend.Cutout;
        return Blend.Opaque;
    }

    // The material's shader reference (top-level m_Shader PPtr): (0, pathId) for a shader serialized in
    // the same file, a non-zero file id for external/built-in shaders (e.g. Unity's Standard).
    public static (int fileId, long pathId) GetShader(Dictionary<string, object> material)
    {
        if (!material.TryGetValue("m_Shader", out object? sh) || sh is not Dictionary<string, object> pptr)
            return (0, 0);
        return (System.Convert.ToInt32(pptr["m_FileID"]), System.Convert.ToInt64(pptr["m_PathID"]));
    }

    // The internal file id and path id of the texture bound to a property, (0, 0) when unset.
    public static (int fileId, long pathId) GetTexture(Dictionary<string, object> material, string property)
    {
        if (!material.TryGetValue("m_SavedProperties", out object? sp) ||
            sp is not Dictionary<string, object> saved ||
            !saved.TryGetValue("m_TexEnvs", out object? te) ||
            te is not List<object> texEnvs)
        {
            return (0, 0);
        }

        foreach (object entry in texEnvs)
        {
            var pair = (Dictionary<string, object>)entry;
            if ((string)pair["first"] != property)
                continue;
            var value = (Dictionary<string, object>)pair["second"];
            var pptr = (Dictionary<string, object>)value["m_Texture"];
            return (System.Convert.ToInt32(pptr["m_FileID"]), System.Convert.ToInt64(pptr["m_PathID"]));
        }
        return (0, 0);
    }
}
