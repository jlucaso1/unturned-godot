using System.Collections.Generic;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;

namespace UnturnedGodot;

// The NPC characters a map places (Russia's forty shopkeepers, guards and quest-givers).
//
// An NPC asset ships no prefab: `Bundles/NPCs/Characters/<name>/Asset.dat` names the clothing and face a
// character wears, and Unturned dresses its ordinary player rig with them. So there is nothing for the
// mesh pipeline to extract, and every one of these placements fell through to a placeholder box.
//
// Here they get the player rig instead, imported once and cloned per placement the way ZombiesView already
// populates a horde from two templates. What they do NOT get yet is their clothing: Shirt/Pants/Hat name
// item assets whose meshes are a separate family this does not read, so every NPC currently stands in the
// character's default look, facing the way the map author pointed it.
public static class NpcsBuilder
{
    // What a standing character plays when it is doing nothing, which is all an NPC does here.
    private const string IdleClip = "Idle_Stand";

    // Builds a root holding one character per placement. `placements` is expected to be only the NPC ones
    // (see Partition); anything whose clone fails is skipped rather than boxed, because a box is what this
    // exists to stop drawing.
    // Null for a map that places none, which is every official map except Russia: an empty node in the
    // scene is still a node, and the world a map builds should not gain one for a feature it never uses.
    public static Node3D? Build(IReadOnlyList<PlacedObject> placements, string unturnedPath,
        out int rendered)
    {
        rendered = 0;
        if (placements.Count == 0)
            return null;

        var root = new Node3D { Name = "Npcs" };
        root.AddToGroup(SceneGroups.Npcs);

        // One import for the whole map: reading the player rig out of resources.assets costs ~100 ms and
        // tens of MB of transient heap, and every NPC is the same rig underneath.
        Node3D? template = CharacterModel.Build(unturnedPath);
        if (template == null)
        {
            Log.Print($"[unturned-godot] NPCs: {placements.Count} placed, but the character rig could not "
                + "be imported; they are not drawn.");
            return root;
        }

        foreach (PlacedObject placement in placements)
        {
            if (CharacterModel.Clone(template) is not { } body)
                continue;

            // A fresh clone has the template's clips but none of them selected, so it would stand in the
            // bind pose. ZombiesView plays a clip on every clone for the same reason.
            (body as CharacterSkeleton)?.Play(IdleClip);

            var npc = new Node3D { Name = "Npc", Transform = ObjectPlacement.ComputeTransform(placement) };
            npc.AddChild(body);
            root.AddChild(npc);
            rendered++;
        }

        template.QueueFree(); // the clones share its meshes and clips; the template itself never enters the tree
        Log.Print($"[unturned-godot] NPCs: {rendered}/{placements.Count} drawn as characters");
        return root;
    }
}
