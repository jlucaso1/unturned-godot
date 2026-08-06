using System.Collections.Generic;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

// The physics material a collider carries, read off the shipped masterbundle. This is the far end of the
// impact chain: PhysicsMaterialAsset names the same surfaces, and the sound and mark a melee hit produces
// are looked up by exactly this string.
//
// A ported reader can be wrong in two ways here, and neither shows up as an exception — a punch just goes
// quiet. Either the m_Material pointer is never resolved (every collider reads as bare), or the game moves
// its PhysicMaterials into another file and the pointer stops resolving locally. Both are asserted.
[Trait("Category", "RealData")]
public class PrefabColliderMaterialRealTests
{
    private static IEnumerable<ColliderPart> AllColliders(PrefabGraph graph)
    {
        foreach (List<ColliderPart> colliders in graph.CollidersByKey.Values)
            foreach (ColliderPart collider in colliders)
                yield return collider;
    }

    // The names the shipped content actually hangs off the colliders of PLACED prefabs. Not the whole of
    // Bundles/Assets/PhysicsMaterials — several of those are terrain-only, and Flesh belongs to the
    // character prefabs, which have no objects/ key and so never appear here — but every one below has to
    // resolve, because these are the surfaces a fist can land on out in the world.
    [RealDataFact(RequiresMasterBundle = true)]
    public void CollidersNameTheShippedPhysicsMaterials()
    {
        var counts = new Dictionary<string, int>();
        foreach (ColliderPart collider in AllColliders(GameData.Prefabs))
        {
            if (collider.MaterialName.Length > 0)
                counts[collider.MaterialName] = counts.GetValueOrDefault(collider.MaterialName) + 1;
        }

        // Asserted as a set rather than by count: the counts move whenever Valve ships a new prop, but a
        // surface disappearing means the resolution broke rather than that the content changed.
        foreach (string expected in new[]
                 {
                     "Wood_Static", "Metal_Static", "Concrete_Static", "Cloth_Static",
                     "Gravel_Static", "Tile_Static", "Foliage_Static",
                 })
        {
            Assert.True(counts.ContainsKey(expected),
                $"no collider in the bundle resolved to {expected}; " +
                $"resolved surfaces were: {string.Join(", ", counts.Keys)}");
        }
    }

    // Most colliders on a placed prefab DO name a surface — measured at ~82%, which is what makes the
    // impact chain worth having at all. The rest carry none, and that is not a failure to read one:
    // Unity's sharedMaterial is null there and PlayerEquipment.punch skips the impact entirely.
    //
    // Pinned as a proportion in both directions. A reader that quietly stopped resolving pointers would
    // read every collider as bare and sink the lower bound; one that started inventing a name for the bare
    // ones would break the upper.
    [RealDataFact(RequiresMasterBundle = true)]
    public void MostCollidersOnPlacedPrefabsNameASurface()
    {
        int total = 0;
        int named = 0;
        foreach (ColliderPart collider in AllColliders(GameData.Prefabs))
        {
            total++;
            if (collider.MaterialName.Length > 0)
                named++;
        }

        Assert.True(total > 1000, $"the bundle should carry thousands of colliders, found {total}");
        Assert.InRange(named / (double)total, 0.6, 0.95);
    }

    // Every m_Material pointer in the shipped bundle resolves inside that same SerializedFile. If the game
    // ever moves its PhysicMaterials into a separate file, the port cannot follow the pointer from here and
    // would silently lose every impact sound — so the day that happens should be a red test, not a quiet
    // regression noticed months later.
    [RealDataFact(RequiresMasterBundle = true)]
    public void EveryColliderMaterialResolvesWithinTheSameFile()
    {
        PrefabGraph graph = GameData.Prefabs;

        var pointers = 0;
        var unresolved = 0;
        foreach (SerializedObject o in graph.File.Objects)
        {
            if (o.ClassId is not (65 or 135 or 136 or 64)) // Box/Sphere/Capsule/Mesh collider
                continue;
            Dictionary<string, object> fields =
                TypeTreeReader.Read(o.TypeTree, graph.File.ReaderFor(o));
            if (!fields.TryGetValue("m_Material", out object? pointer))
                continue;

            var pptr = (Dictionary<string, object>)pointer;
            if (System.Convert.ToInt64(pptr["m_PathID"]) == 0)
                continue;
            pointers++;
            if (System.Convert.ToInt32(pptr["m_FileID"]) != 0
                || !graph.ObjectsByPathId.ContainsKey(System.Convert.ToInt64(pptr["m_PathID"])))
            {
                unresolved++;
            }
        }

        Assert.True(pointers > 100, $"expected thousands of material pointers, found {pointers}");
        Assert.Equal(0, unresolved);
    }
}
