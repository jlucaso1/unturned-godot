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

    // Glass and other blended materials use a Transparent render queue (>= 3000) or a Fade/Transparent
    // _Mode (2 or 3) on Unity's Standard shader; without this they render opaque with a garbled look.
    public static bool IsTransparent(Dictionary<string, object> material)
    {
        if (material.TryGetValue("m_CustomRenderQueue", out object? rq) && System.Convert.ToInt32(rq) >= 3000)
            return true;
        float mode = GetFloat(material, "_Mode") ?? 0f;
        return mode == 2f || mode == 3f;
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
