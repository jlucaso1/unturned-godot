using System;
using System.Collections.Generic;

namespace UnturnedGodot.Unity;

// How a shader itself says its output blends, read from the tags in its serialized parsed form — the
// same "Tags { Queue = AlphaTest RenderType = TransparentCutout }" block the shader author wrote.
//
// This matters for shaders outside Unity's Standard family. A material only carries _Mode when it was
// authored against Standard, and it keeps whatever value it had when the shader was swapped: workshop
// foliage materials sit on a Custom/Foliage shader that alpha-clips, while still storing _Mode = 0. Read
// as Standard that says "opaque", and the foliage cards render as solid triangles.
public static class UnityShaderBlend
{
    // The blend the shader declares, or null when it says nothing (the caller then keeps the material's
    // own answer). Reads the subshader tags, falling back to the tags of its passes.
    public static UnityMaterial.Blend? Read(Dictionary<string, object> shader)
    {
        if (!shader.TryGetValue("m_ParsedForm", out object? pf) || pf is not Dictionary<string, object> parsed
            || !parsed.TryGetValue("m_SubShaders", out object? subs) || subs is not List<object> subShaders)
        {
            return null;
        }

        foreach (object entry in subShaders)
        {
            if (entry is not Dictionary<string, object> subShader)
                continue;

            if (FromTags(Tags(subShader)) is { } declared)
                return declared;

            if (subShader.TryGetValue("m_Passes", out object? ps) && ps is List<object> passes)
                foreach (object p in passes)
                    if (p is Dictionary<string, object> pass && FromTags(Tags(pass)) is { } fromPass)
                        return fromPass;
        }

        return null;
    }

    // Unity stores tags as a list of first/second string pairs under m_Tags.tags.
    private static IEnumerable<(string Key, string Value)> Tags(Dictionary<string, object> owner)
    {
        if (!owner.TryGetValue("m_Tags", out object? t) || t is not Dictionary<string, object> tags
            || !tags.TryGetValue("tags", out object? list) || list is not List<object> entries)
        {
            yield break;
        }

        foreach (object entry in entries)
            if (entry is Dictionary<string, object> pair
                && pair.TryGetValue("first", out object? key) && pair.TryGetValue("second", out object? value))
            {
                // Convert.ToString maps a null value to the empty string, so neither can be null here.
                yield return (Convert.ToString(key)!, Convert.ToString(value)!);
            }
    }

    // RenderType names the surface kind; Queue names the render order. Either is enough, and both are
    // written by the shader author. "TransparentCutout" is checked before "Transparent" because the
    // former starts with the latter.
    private static UnityMaterial.Blend? FromTags(IEnumerable<(string Key, string Value)> tags)
    {
        foreach ((string key, string value) in tags)
        {
            if (key.Equals("RenderType", StringComparison.OrdinalIgnoreCase))
            {
                if (value.StartsWith("TransparentCutout", StringComparison.OrdinalIgnoreCase))
                    return UnityMaterial.Blend.Cutout;
                if (value.StartsWith("Transparent", StringComparison.OrdinalIgnoreCase))
                    return UnityMaterial.Blend.Alpha;
            }
            else if (key.Equals("QUEUE", StringComparison.OrdinalIgnoreCase))
            {
                // Queues can carry an offset ("AlphaTest+50"); the name before it is what matters.
                if (value.StartsWith("AlphaTest", StringComparison.OrdinalIgnoreCase))
                    return UnityMaterial.Blend.Cutout;
                if (value.StartsWith("Transparent", StringComparison.OrdinalIgnoreCase))
                    return UnityMaterial.Blend.Alpha;
            }
        }

        return null;
    }
}
