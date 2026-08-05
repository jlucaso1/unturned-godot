using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// The materials realised for one load, plus which mesh surface took each of them.
//
// Sharing is by complete material state — texture identity, colour, blend, metallic, smoothness, cull —
// so every surface that resolves to the same state gets one StandardMaterial3D (or one cutout
// ShaderMaterial) instead of its own.
//
// The texture identity is the catch. It is the hash of the .tex file, and on a cold load none of those
// exist yet: the materials are built between the mesh phase and the texture phase of the same decode pass,
// so every key stands in for its own identity and byte-identical textures build a material each. That is
// why the surfaces are recorded here — once the textures have settled, Rededuplicate re-runs the grouping
// with the real identities and points the aliases at one material, which is what a warm load had all along.
public sealed class MaterialTable
{
    // The complete material state, with the texture named by identity rather than by cache key.
    internal readonly record struct Key(string TextureIdentity, Color Color, UnityMaterial.Blend Blend,
        float Metallic, float Smoothness, EShaderCull Cull);

    // The material state as authored, i.e. still naming its texture by cache key: what a surface has to be
    // re-grouped by once the key's identity is known.
    private readonly record struct State(Color Color, UnityMaterial.Blend Blend, float Metallic,
        float Smoothness, EShaderCull Cull);

    private readonly record struct Surface(ArrayMesh Mesh, int Index, string TextureKey, State State);

    private readonly Dictionary<Key, Material> _byKey = new();
    private readonly List<Surface> _surfaces = new();
    private bool _tracking;

    public int Count => _byKey.Count;

    // Records where each material lands, so it can be re-grouped later. Only the cold path asks for this:
    // a warm load resolves every identity up front and has nothing left to merge, and the record would
    // otherwise hold a reference to every realised mesh for the rest of the load.
    public void TrackSurfaces() => _tracking = true;

    internal bool TryGet(in Key key, out Material material)
    {
        _byKey.TryGetValue(key, out Material? found);
        material = found!;
        return found != null;
    }

    internal void Add(in Key key, Material material) => _byKey[key] = material;

    internal void Track(ArrayMesh mesh, int index, in CachedSubmesh submesh)
    {
        if (_tracking && submesh.TextureKey.Length > 0)
        {
            _surfaces.Add(new Surface(mesh, index, submesh.TextureKey, new State(submesh.Color,
                submesh.Blend, submesh.Metallic, submesh.Smoothness, submesh.Cull)));
        }
    }

    // Main thread. Re-groups every tracked surface by the identities the registry now knows and points the
    // aliases at the surviving material. Byte-identical textures decode to one ImageTexture already
    // (TextureRegistry deduplicates those by content), so the materials being merged carry the same map,
    // the same filter and the same shader — the swap is invisible, and the duplicates become garbage.
    public (int Repointed, int Materials) Rededuplicate(TextureRegistry registry)
    {
        if (_surfaces.Count == 0)
            return (0, _byKey.Count);

        var keys = new List<(string TextureKey, State State)>(_surfaces.Count);
        foreach (Surface surface in _surfaces)
            keys.Add((surface.TextureKey, surface.State));

        int[] canonical = MaterialAliasPlan.Canonical(keys, registry.MaterialIdentities);
        int repointed = 0;
        for (int i = 0; i < _surfaces.Count; i++)
        {
            int elected = canonical[i];
            if (elected == i)
                continue;

            Surface surface = _surfaces[i];
            Surface winner = _surfaces[elected];
            Material? target = winner.Mesh.SurfaceGetMaterial(winner.Index);
            if (target == null || target == surface.Mesh.SurfaceGetMaterial(surface.Index))
                continue;

            surface.Mesh.SurfaceSetMaterial(surface.Index, target);
            // A texture that never applied — an unsupported format, say — leaves its key pending forever.
            // Keeping the surviving material on that list means a late apply still reaches this surface.
            registry.RegisterAlias(surface.TextureKey, target);
            repointed++;
        }

        // What is left: one material per surviving group, plus the untextured ones the tracked set never
        // covers (an untextured submesh has no key to resolve, so it can neither gain nor lose an alias).
        (_, int groups) = MaterialAliasPlan.Cost(canonical);
        int untextured = 0;
        foreach (Key key in _byKey.Keys)
            if (key.TextureIdentity.Length == 0)
                untextured++;
        return (repointed, groups + untextured);
    }

    // Drops the load-time index once there is nothing left to realise or re-group. The surface record
    // holds a reference to every realised mesh and the key table one to every material; the scene owns
    // both from here on, and a table asked for a material after this would build a second one.
    public void Release()
    {
        _surfaces.Clear();
        _surfaces.TrimExcess();
        _byKey.Clear();
        _tracking = false;
    }
}
