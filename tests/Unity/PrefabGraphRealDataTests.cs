using System.Collections.Generic;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

// What the shipped masterbundle's prefabs resolve to. These are the end of the chain PrefabPartsTests
// checks in isolation: the same rules, run over the real object graph.
//
// Unturned keeps a destructible object's alternate states in one prefab — "Alive" is what a placed object
// shows, "Dead" the wreck, "Ragdoll" the debris — and enables exactly one. The port used to read all three
// as ordinary geometry, so a bench drew its wreck, a computer drew its debris on top of itself, and every
// ragdoll capsule became solid world collision.
[Trait("Category", "RealData")]
public class PrefabGraphRealDataTests
{
    private static string MeshName(PrefabGraph graph, long meshId)
    {
        Assert.True(graph.ObjectsByPathId.TryGetValue(meshId, out SerializedObject? mesh),
            $"the graph points at mesh {meshId}, which the file does not contain");
        return (string)TypeTreeReader.Read(mesh!.TypeTree, graph.File.ReaderFor(mesh))["m_Name"];
    }

    private static List<MeshPart> Parts(PrefabGraph graph, string key)
    {
        Assert.True(graph.PartsByKey.TryGetValue(key, out List<MeshPart>? parts),
            $"the bundle has no renderable parts for {key}");
        return parts!;
    }

    // Bench #1 and Transit #1 name their meshes after the state they belong to, so the shipped bundle can
    // say outright which model the port picked. Both used to come out as the wreck.
    [RealDataTheory(RequiresMasterBundle = true)]
    [InlineData("objects/medium/parks/bench_wood_0")]
    [InlineData("objects/medium/business/transit_0")]
    public void RealPrefabs_DrawTheLiveModel(string key)
    {
        PrefabGraph graph = GameData.Prefabs;

        MeshPart part = Assert.Single(Parts(graph, key));
        Assert.Equal("Model_0_Alive", MeshName(graph, part.MeshId));
    }

    // Computer #1 keeps a full Model_0/Model_1 pair under BOTH Alive and Ragdoll, so reading the states as
    // ordinary parts drew the debris inside the live model at every distance.
    [RealDataFact(RequiresMasterBundle = true)]
    public void RealPrefabs_DoNotDrawARagdollOverTheLiveModel()
    {
        PrefabGraph graph = GameData.Prefabs;
        const string key = "objects/medium/business/computer_0";

        Assert.Single(Parts(graph, key));
        Assert.Single(graph.Lod1PartsByKey[key]);
    }

    // Camera #1 is the Tutorial map's most-placed object (20 of its 53 placements). Its Alive and Dead
    // models both hang straight off the prefab root, so which one the port drew came down to the order the
    // two MeshFilters happen to sit in the file — and it drew the broken one.
    [RealDataFact(RequiresMasterBundle = true)]
    public void RealPrefabs_DrawTheIntactCamera()
    {
        PrefabGraph graph = GameData.Prefabs;

        MeshPart part = Assert.Single(Parts(graph, "objects/small/furniture/camera_0"));
        Assert.Equal("Model_0", MeshName(graph, part.MeshId)); // "Model_1" is the smashed one
    }

    // The ragdoll's capsules are collision for debris the game throws, never for the standing object. Left
    // in, they stood in the world as shapes with nothing drawn where they were.
    [RealDataTheory(RequiresMasterBundle = true)]
    [InlineData("objects/medium/furniture/chair_wood_0")]  // was Mesh + a ragdoll Capsule
    [InlineData("objects/medium/parks/inukshuk")]          // was Mesh + a ragdoll Capsule
    [InlineData("objects/medium/parks/bench_wood_0")]      // was Mesh + two ragdoll Capsules
    public void RealPrefabs_TakeNoCollisionFromAHiddenState(string key)
    {
        PrefabGraph graph = GameData.Prefabs;

        ColliderPart collider = Assert.Single(graph.CollidersByKey[key]);
        Assert.Equal(EColliderKind.Mesh, collider.Kind);
    }

    // Nothing in the shipped content is built ONLY out of hidden states, so excluding them can never leave
    // a prefab with no model at all. Asserted over the whole bundle rather than the handful above: a game
    // update that reorganised a prefab into a state node would show up here.
    [RealDataFact(RequiresMasterBundle = true)]
    public void RealPrefabs_KeepEveryPrefabThatHasAModel()
    {
        PrefabGraph graph = GameData.Prefabs;

        Assert.True(graph.PartsByKey.Count > 1000,
            $"only {graph.PartsByKey.Count} prefabs resolved to a model");
        foreach (KeyValuePair<string, List<MeshPart>> entry in graph.PartsByKey)
            Assert.False(entry.Value.Count == 0, $"{entry.Key} resolved to no parts at all");
    }

    // The two ladder objects the game ships. Each has exactly two colliders: the mesh the player walks
    // into, and the climbing volume on Unity's LADDER layer — which is a trigger there, so building it as
    // solid geometry (which is what happened before the layer was read) put an invisible 15 cm slab in
    // front of every ladder in the world.
    [RealDataTheory(RequiresMasterBundle = true)]
    [InlineData("objects/medium/furniture/ladder_wood_0")]
    [InlineData("objects/medium/furniture/ladder_metal_0")]
    public void RealPrefabs_MarkALaddersClimbingVolumeAndNothingElse(string key)
    {
        PrefabGraph graph = GameData.Prefabs;

        List<ColliderPart> colliders = graph.CollidersByKey[key];
        Assert.Equal(2, colliders.Count);

        ColliderPart volume = Assert.Single(colliders.FindAll(c => UnityLayers.IsLadder(c.Layer)));
        Assert.Equal(EColliderKind.Box, volume.Kind);
        Assert.Equal(UnityLayers.Ladder, volume.Layer);
        // Authored lying along its own Z: 1.15 m of rung, 15 cm thick, 6.75 m of climb.
        Assert.Equal(6.75f, volume.Size.Z, 2);
        Assert.True(volume.Size.Y < volume.Size.X, "the volume's thin axis is the one it faces along");

        ColliderPart solid = Assert.Single(colliders.FindAll(c => !UnityLayers.IsLadder(c.Layer)));
        Assert.Equal(EColliderKind.Mesh, solid.Kind);
    }

    // Nothing else in the shipped content claims to be a ladder, and every collider that does sits on a
    // GameObject called "Ladder": the layer and the tag agree, which is what lets the layer stand in for
    // the tag a built AssetBundle does not carry.
    [RealDataFact(RequiresMasterBundle = true)]
    public void RealPrefabs_PutNoOrdinaryColliderOnTheLadderLayer()
    {
        PrefabGraph graph = GameData.Prefabs;

        var ladderKeys = new List<string>();
        foreach (KeyValuePair<string, List<ColliderPart>> entry in graph.CollidersByKey)
            if (entry.Value.Exists(c => UnityLayers.IsLadder(c.Layer)))
                ladderKeys.Add(entry.Key);

        ladderKeys.Sort(System.StringComparer.Ordinal);
        Assert.Equal(
            new[] { "objects/medium/furniture/ladder_metal_0", "objects/medium/furniture/ladder_wood_0" },
            ladderKeys);
    }

    // The Tutorial map's one building: a single unlevelled part with both its submesh materials, which is
    // what the fallback has to keep whole.
    [RealDataFact(RequiresMasterBundle = true)]
    public void RealPrefabs_KeepTheTutorialBuildingWhole()
    {
        PrefabGraph graph = GameData.Prefabs;

        MeshPart part = Assert.Single(Parts(graph, "objects/large/buildings/tutorial_0"));
        Assert.Equal(2, part.Materials.Count);
        Assert.Single(graph.CollidersByKey["objects/large/buildings/tutorial_0"]);
    }

    // Every renderable part of a prefab has to resolve into ONE frame — the prefab's own — whether it
    // hangs off a MeshFilter or a SkinnedMeshRenderer.
    //
    // These three carry both kinds. Their static geometry sits on a "Model_0" whose -90 degrees about X
    // cancel the +90 its "Root" parent carries; their glass, hinges, hobs and counter tops are skinned,
    // and Unity poses those from the bones alone, so the renderer's own chain never applies to them. The
    // graph used to compose that chain for the skinned ones too, handing them the +90 that nothing
    // cancelled: PEI's diner showed its display cases with the glass lying flat outside the building, and
    // its ovens and counters with their tops thrown clear of the bodies.
    [RealDataTheory(RequiresMasterBundle = true)]
    [InlineData("objects/medium/business/cooler_0")]
    [InlineData("objects/medium/furniture/counter_2")]
    [InlineData("objects/medium/furniture/oven_0")]
    public void RealPrefabs_ResolveSkinnedAndStaticPartsIntoOneFrame(string key)
    {
        List<MeshPart> parts = Parts(GameData.Prefabs, key);

        Assert.True(parts.Count > 1, $"{key} is expected to carry several parts");
        foreach (MeshPart part in parts)
        {
            // Not Assert.Equal on the transform: the authored quaternions round-trip through floats, so
            // the cancelling pair lands a rounding step off exact identity.
            Godot.Basis basis = part.LocalToRoot.Basis;
            Assert.Equal(Godot.Vector3.Zero, part.LocalToRoot.Origin);
            Assert.True(basis.X.IsEqualApprox(Godot.Vector3.Right)
                && basis.Y.IsEqualApprox(Godot.Vector3.Up)
                && basis.Z.IsEqualApprox(Godot.Vector3.Back),
                $"{key} has a part rotated out of the prefab frame: {basis}");
        }
    }

    // The same thing said in the geometry rather than the transform, so the assertion above cannot pass on
    // a graph that has quietly stopped finding the skinned parts at all: the pane really does stand inside
    // the case it belongs to. Cooler_0's body is 2.5 units tall in the authored (Z-up) frame and its glass
    // spans that same height just outside the front face — which is only true while both share a frame.
    [RealDataFact(RequiresMasterBundle = true)]
    public void RealCooler_KeepsItsGlassInsideTheCase()
    {
        PrefabGraph graph = GameData.Prefabs;
        var heights = new List<float>();

        foreach (MeshPart part in Parts(graph, "objects/medium/business/cooler_0"))
        {
            UnityMesh mesh = UnityMesh.Read(TypeTreeReader.Read(
                graph.ObjectsByPathId[part.MeshId].TypeTree,
                graph.File.ReaderFor(graph.ObjectsByPathId[part.MeshId])));
            if (!mesh.Usable)
                continue;

            float min = float.MaxValue, max = float.MinValue;
            foreach (Godot.Vector3 v in mesh.Vertices)
            {
                float z = (part.LocalToRoot * v).Z;
                min = System.Math.Min(min, z);
                max = System.Math.Max(max, z);
            }
            heights.Add(max - min);
        }

        Assert.NotEmpty(heights);
        // Every part spans most of the case's height. Applying the chain to the skinned ones turned that
        // extent into a 0.2-unit-thick slab lying across the Z axis instead.
        Assert.All(heights, h => Assert.InRange(h, 2.0f, 2.6f));
    }
}
