using System;
using System.Collections.Generic;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;

namespace UnturnedGodot.Damage;

// Turns the level's placements into the server's ledger of what can be broken.
//
// The input is the SAME list the collision builder works from — LevelObjects.Load plus the trees
// LegacyPlacements.AppendTrees folds into it — so an index into that list is a stable name for a
// placement, and the ledger and the physics world are talking about the same things in the same order.
//
// Only placements whose asset says they have hit points are kept. On PEI that is every tree and rock
// plus the scattered rubble props, and none of the thousands of purely decorative objects: a punch's
// lookup then walks a list of things that can actually break rather than the whole map.
public static class DamageableWorldBuilder
{
    public static DamageableWorld Build(IReadOnlyList<PlacedObject> placements, ObjectAssetDatabase database)
    {
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(database);

        var world = new DamageableWorld();
        for (int i = 0; i < placements.Count; i++)
        {
            PlacedObject placement = placements[i];
            ObjectAsset? asset = database.Resolve(placement.Guid, placement.Id);
            if (asset == null)
                continue;
            if (!TryDescribe(placement, asset, out DamageableInstance instance))
                continue;
            world.Add(instance);
        }
        return world;
    }

    // Whether this placement is breakable, and as what. A resource is one by its Health alone; an object
    // has to carry a rubble block, which is what separates a garbage pile from the wall behind it.
    public static bool TryDescribe(in PlacedObject placement, ObjectAsset asset,
        out DamageableInstance instance)
    {
        ArgumentNullException.ThrowIfNull(asset);
        instance = default;
        // Landscape.UnityToGodot, through the same transform the collider for this placement is built
        // with: the ledger has to agree with the physics world about where the thing stands, and the
        // only way to guarantee that is to compute it the same way.
        Godot.Vector3 position = ObjectPlacement.ComputeTransform(placement).Origin;

        DamageableInstance candidate = asset.Type == EObjectType.Resource
            ? DamageableInstance.Resource(position, asset)
            : DamageableInstance.Object(position, asset);
        if (asset.Type != EObjectType.Resource && !asset.HasRubble)
            return false;
        if (!candidate.IsDamageable)
            return false;

        instance = candidate;
        return true;
    }
}
