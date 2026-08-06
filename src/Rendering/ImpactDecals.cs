using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// The mark a hit leaves on a surface — the decal half of DamageTool.ReceiveSpawnLegacyImpact.
//
// The original instantiates the effect's whole prefab: a particle burst, a decal quad, and for gore a
// scattering of splatters. This port draws the DECAL alone, which is the part that persists and the part a
// player actually reads as "I hit that". The particles are a Unity ParticleSystem with a dozen animated
// modules, and approximating them would be guesswork where the decal is the shipped texture itself.
//
// Which texture is entirely data-driven: the surface names a PhysicsMaterialAsset, that names an
// EffectAsset by GUID, and that effect's folder in the master bundle holds the mark. Nothing here knows
// what concrete looks like.
public sealed partial class ImpactDecals : Node3D
{
    // How long a mark lasts. GraphicsSettings.effect at HIGH quality is Random.Range(40, 56) seconds, and
    // the game's own decals are destroyed on exactly that timer.
    public const float LifetimeSeconds = 48f;
    public const float LifetimeSpread = 8f;

    // How wide the projection is. The game's decal quad is authored on the effect prefab, which is not
    // read here; a fist-sized mark is a quarter of a metre and that is what these textures were drawn for.
    public const float SizeMetres = 0.25f;

    // How far the decal box reaches into and out of the surface. Thin, so it lands on the wall it was
    // aimed at rather than bleeding through onto whatever is behind it.
    public const float DepthMetres = 0.1f;

    // How many marks can exist at once. A punch every half second for the 48 seconds a mark lives is
    // ~96 of them, and a Godot Decal is a projection pass rather than a sprite — so this is a real
    // ceiling rather than a formality. The oldest is recycled, which is what a fixed pool in the original
    // does too (EffectManager pools its effects and reuses the least recently spawned).
    public const int MaxDecals = 24;

    private readonly PhysicsMaterialBank _materials;
    private readonly ImpactEffectBank _effects;
    private readonly Func<ImpactEffectAsset, string> _bundleTagOf;
    private readonly string _cacheDirectory;
    private readonly RandomNumberGenerator _rng = new();

    // Loaded marks, by cache key. A texture that is absent caches as null so a surface whose extraction
    // never ran is not re-read from disk on every swing.
    private readonly Dictionary<string, Texture2D?> _textures = new(StringComparer.Ordinal);

    private readonly List<Decal> _pool = new();
    private readonly List<double> _expiry = new();

    // Where the round-robin stands once the pool is full.
    private int _next;

    // Marks that resolved to no texture at all: a surface that names no effect, or one whose textures
    // never made it into the cache. Read by the diagnostics; silence here is legitimate.
    public int UnmarkedImpacts { get; private set; }

    public ImpactDecals(PhysicsMaterialBank materials, ImpactEffectBank effects,
        Func<ImpactEffectAsset, string> bundleTagOf, string cacheDirectory)
    {
        _materials = materials ?? throw new ArgumentNullException(nameof(materials));
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
        _bundleTagOf = bundleTagOf ?? throw new ArgumentNullException(nameof(bundleTagOf));
        _cacheDirectory = cacheDirectory ?? throw new ArgumentNullException(nameof(cacheDirectory));
        Name = "ImpactDecals";
    }

    // Marks `surface` at `point`, facing `normal`. False when that surface leaves no mark, which is a
    // real answer: Foliage, Snow and Water name effects that are particles only.
    public bool Mark(string surface, Vector3 point, Vector3 normal)
    {
        Texture2D? texture = TextureFor(surface);
        if (texture == null)
        {
            UnmarkedImpacts++;
            return false;
        }

        Decal decal = Claim();
        decal.TextureAlbedo = texture;
        decal.Size = new Vector3(SizeMetres, DepthMetres, SizeMetres);
        // ServerTriggerImpactEffectForMagazinesV2 nudges the effect out of the surface by 4-6 cm before
        // spawning it, and the old impact path did the same. Without it the mark z-fights the wall.
        decal.GlobalPosition = point + (normal * _rng.RandfRange(0.04f, 0.06f));
        decal.GlobalBasis = FacingBasis(normal, _rng.RandfRange(0f, Mathf.Tau));
        decal.Visible = true;
        return true;
    }

    // A Decal projects down its own -Y, so the mark faces the surface when +Y is the normal. The spin
    // about that axis is free variation the game gets from its four splatter prefabs and its randomized
    // rotation flag; without it, repeated punches stamp an identical mark.
    public static Basis FacingBasis(Vector3 normal, float spinRadians)
    {
        Vector3 up = normal.LengthSquared() > 1e-6f ? normal.Normalized() : Vector3.Up;
        // Any perpendicular will do; pick the axis the normal is least aligned with so the cross product
        // is well conditioned even on a floor or a wall exactly on an axis.
        Vector3 reference = MathF.Abs(up.Y) > 0.99f ? Vector3.Forward : Vector3.Up;
        Vector3 right = reference.Cross(up).Normalized();
        // X cross Y, in that order. The other way round gives a left-handed basis with determinant -1,
        // which Godot renders as a mirrored projection rather than refusing — so it is a bug that looks
        // almost right and only the determinant catches.
        Vector3 forward = right.Cross(up);
        return new Basis(right, up, forward).Rotated(up, spinRadians);
    }

    // The mark for a surface, or null. Resolution is the fallback walk both banks share: surface ->
    // effect -> that effect's textures, one of which is picked at random for a gore effect exactly as the
    // game picks between its four splatter prefabs.
    private Texture2D? TextureFor(string surface)
    {
        if (string.IsNullOrEmpty(surface))
            return null;
        Guid guid = _materials.FindImpactEffect(surface);
        if (guid == Guid.Empty || _effects.Find(guid) is not { } effect)
            return null;

        string tag = _bundleTagOf(effect);

        // Gathered first, THEN chosen from — rather than starting at a random candidate and taking the
        // first that loads, which was the first attempt and is not uniform: most candidates do not exist
        // (both folder shapes are offered for every effect), so whichever real texture follows the most
        // dead entries wins far more often than the others. A gore effect has four real splatters and
        // should show all four evenly.
        //
        // The list is cached per effect: a punch is rare, but this walks nine candidate paths and there
        // is no reason to do it twice for the same surface.
        if (!_resolved.TryGetValue(effect.Guid, out List<Texture2D>? textures))
        {
            textures = new List<Texture2D>();
            foreach (string candidate in effect.DecalTextureCandidates)
                if (Load(ImpactDecalPlan.CacheKey(tag, candidate)) is { } texture)
                    textures.Add(texture);

            // Only cached once something resolved: until the deferred extraction has run, every candidate
            // misses, and caching that would leave the surface unmarked for the session.
            if (textures.Count == 0)
                return null;
            _resolved[effect.Guid] = textures;
        }

        return textures[_rng.RandiRange(0, textures.Count - 1)];
    }

    // The textures each effect actually has, once any of them has been found.
    private readonly Dictionary<Guid, List<Texture2D>> _resolved = new();

    private Texture2D? Load(string cacheKey)
    {
        if (_textures.TryGetValue(cacheKey, out Texture2D? cached))
            return cached;

        Texture2D? texture = null;
        // Through the extractor's own path builder, so a cache written under one layout can never be
        // looked for under another.
        string path = ImpactDecalExtractor.PathFor(_cacheDirectory, cacheKey);
        if (File.Exists(path) && TextureCache.IsCurrent(path))
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                texture = ModelLibrary.BuildTexture(CachedTexture.Decoded(TextureCache.Read(stream)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                or InvalidDataException)
            {
                texture = null; // a corrupt entry re-extracts on the next boot
            }
        }

        // A HIT is cached; a miss is not. The textures are extracted on the deferred pass, several
        // seconds after the first punch is possible, so caching a miss would leave those surfaces
        // unmarked for the rest of the session. Punches are rare enough that the retry costs nothing —
        // and once a surface has resolved once, it never touches the filesystem again.
        if (texture != null)
            _textures[cacheKey] = texture;
        return texture;
    }

    // The next decal to use: a fresh one while the pool is filling, then round-robin.
    //
    // Round-robin rather than "whichever expires soonest", which is what this did first and was wrong:
    // each mark's lifetime carries a random spread, so the expiry order is not the creation order, and
    // picking the soonest could land on a slot that had just been claimed — a burst of punches then
    // overwrote two or three slots repeatedly while older marks stood untouched. Every mark lives about
    // the same time, so the oldest IS the next one round, and finding it costs nothing.
    //
    // Internal rather than private so the bound can be tested for what it is — a property of the pool,
    // not of whether a mark resolved to a texture.
    internal Decal Claim()
    {
        double until = (Time.GetTicksMsec() / 1000.0) + LifetimeSeconds
            + _rng.RandfRange(-LifetimeSpread, LifetimeSpread);

        if (_pool.Count < MaxDecals)
        {
            var created = new Decal { Visible = false };
            AddChild(created);
            _pool.Add(created);
            _expiry.Add(until);
            return created;
        }

        _next = (_next + 1) % _pool.Count;
        _expiry[_next] = until;
        return _pool[_next];
    }

    // Retires marks whose time is up. Runs on the frame loop rather than on a timer per decal, because a
    // pool of two dozen is cheaper to sweep than it is to schedule.
    public override void _Process(double delta)
    {
        double now = Time.GetTicksMsec() / 1000.0;
        for (int i = 0; i < _pool.Count; i++)
            if (_pool[i].Visible && _expiry[i] <= now)
                _pool[i].Visible = false;
    }
}
