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
}
