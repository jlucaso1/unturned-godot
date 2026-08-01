using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// Resolves each submesh's material off a PrefabGraph: the material palette (batched objects) or the
// object's own MeshRenderer material (rocks/trees), then reads its color, blend mode and texture key.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed class MaterialResolver
{
    private readonly PrefabGraph _graph;
    private readonly Dictionary<Guid, MaterialPalette> _palettes;
    private readonly string _bundleTag;
    // Per shader pathId; many materials share one shader, so both reads are cached.
    private readonly Dictionary<long, EShaderCull?> _shaderCulls = new();
    private readonly Dictionary<long, UnityMaterial.Blend?> _shaderBlends = new();

    private MaterialResolver(PrefabGraph graph, Dictionary<Guid, MaterialPalette> palettes, string bundleTag)
    {
        _graph = graph;
        _palettes = palettes;
        _bundleTag = bundleTag;
    }

    public static MaterialResolver Read(PrefabGraph graph, string assetsDir, string bundleTag) =>
        new(graph, ScanPalettes(assetsDir), bundleTag);

    public MaterialPalette? PaletteFor(Guid guid) =>
        _palettes.TryGetValue(guid, out MaterialPalette? p) ? p : null;

    // Reads a submesh's flat color, blend mode, Standard-shader surface response (_Metallic/_Glossiness),
    // the shader's authored culling and (optional) _MainTex texture KEY — this bundle's tag plus the
    // texture path id — without reading pixels (that needs the .resS stream). Also returns the raw texture
    // path id (0 when none) so the caller can grab the texture's metadata for later streaming.
    public (Color color, string texKey, UnityMaterial.Blend blend, long texId, float metallic, float smoothness,
        EShaderCull cull)
        Resolve(int submeshIndex, MaterialPalette? palette, List<long> rendererMaterials)
    {
        long matId = MaterialForSubmesh(submeshIndex, palette, rendererMaterials);
        if (matId == 0 || !_graph.ObjectsByPathId.TryGetValue(matId, out SerializedObject? matObj))
            return (Colors.White, string.Empty, UnityMaterial.Blend.Opaque, 0, 0f, 0f, EShaderCull.Back);

        Dictionary<string, object> matDict = TypeTreeReader.Read(matObj.TypeTree, _graph.File.ReaderFor(matObj));
        Color color = UnityMaterial.GetColor(matDict, "_Color") ?? Colors.White;
        UnityMaterial.Blend blend = BlendOf(matDict);
        float metallic = UnityMaterial.GetFloat(matDict, "_Metallic") ?? 0f;
        float smoothness = UnityMaterial.GetFloat(matDict, "_Glossiness") ?? 0f;
        EShaderCull cull = CullOf(matDict);

        (int fileId, long texId) = UnityMaterial.GetTexture(matDict, "_MainTex");
        bool has = fileId == 0 && texId != 0 && _graph.ObjectsByPathId.ContainsKey(texId);
        return (color, has ? TextureKey.For(_bundleTag, texId) : string.Empty, blend, has ? texId : 0,
            metallic, smoothness, cull);
    }

    // The material's own answer first: an explicit render queue, or a non-opaque Standard _Mode. When it
    // says "opaque" the shader gets the last word, because a material that was moved onto a custom shader
    // keeps the _Mode it had under Standard (workshop foliage is exactly this case).
    private UnityMaterial.Blend BlendOf(Dictionary<string, object> matDict)
    {
        UnityMaterial.Blend fromMaterial = UnityMaterial.GetBlendMode(matDict);
        if (fromMaterial != UnityMaterial.Blend.Opaque)
            return fromMaterial;

        return ShaderOf(matDict, _shaderBlends, UnityShaderBlend.Read) ?? UnityMaterial.Blend.Opaque;
    }

    // The authored culling of the material's shader, read from the shader serialized in the bundle
    // itself (UnityShaderCulling). External/built-in shaders (Unity's own Standard fallback) carry no
    // in-bundle data and cull Back, the engine default.
    private EShaderCull CullOf(Dictionary<string, object> matDict) =>
        ShaderOf<EShaderCull?>(matDict, _shaderCulls, s => UnityShaderCulling.Read(s)) ?? EShaderCull.Back;

    // Reads something off the material's shader, once per shader. External/built-in shaders (Unity's own
    // Standard fallback) carry no in-bundle data, so they answer nothing.
    private T? ShaderOf<T>(Dictionary<string, object> matDict, Dictionary<long, T?> cache,
        Func<Dictionary<string, object>, T?> read)
    {
        (int shaderFileId, long shaderId) = UnityMaterial.GetShader(matDict);
        if (shaderFileId != 0 || shaderId == 0
            || !_graph.ObjectsByPathId.TryGetValue(shaderId, out SerializedObject? shaderObj))
        {
            return default;
        }

        if (cache.TryGetValue(shaderId, out T? cached))
            return cached;

        T? value = read(TypeTreeReader.Read(shaderObj.TypeTree, _graph.File.ReaderFor(shaderObj)));
        cache[shaderId] = value;
        return value;
    }

    // Picks the material id: the palette's material (batched objects) if it covers this submesh, otherwise
    // the object's own MeshRenderer material (rocks/trees have no palette). 0 = none.
    private long MaterialForSubmesh(int submeshIndex, MaterialPalette? palette, List<long>? rendererMaterials)
    {
        if (palette != null && submeshIndex < palette.MaterialPaths.Count)
        {
            string matPath = _graph.AssetPrefix +
                palette.MaterialPaths[submeshIndex].Replace('\\', '/').ToLowerInvariant();
            return _graph.ContainerByPath.TryGetValue(matPath, out long id) ? id : 0;
        }
        if (rendererMaterials != null && submeshIndex < rendererMaterials.Count)
            return rendererMaterials[submeshIndex];
        return 0;
    }

    private static Dictionary<Guid, MaterialPalette> ScanPalettes(string assetsDir)
    {
        var palettes = new Dictionary<Guid, MaterialPalette>();
        if (!Directory.Exists(assetsDir))
            return palettes;

        foreach (string path in Directory.EnumerateFiles(assetsDir, "*.asset", SearchOption.AllDirectories))
        {
            MaterialPalette? palette;
            try { palette = MaterialPalette.Read(DatParser.Parse(File.ReadAllText(path))); }
            catch (IOException) { continue; }
            if (palette != null && palette.MaterialPaths.Count > 0)
                palettes[palette.Guid] = palette;
        }
        return palettes;
    }
}
