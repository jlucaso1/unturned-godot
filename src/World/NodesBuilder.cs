using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Data;

namespace UnturnedGodot;

// Places a floating billboard label at each named location (town/landmark) from Environment/Nodes.dat,
// like Unturned's in-world place names. Labels show through terrain (map-marker style) so a town's name is
// readable even from across the island.
//
// OFF by default, and that is a gameplay decision rather than a performance one: Unturned names a place
// when you ARRIVE somewhere, not by hanging a permanent set of billboards over the island, and a label
// with depth testing switched off so it stays legible from across the map is a view of the level's data
// rather than something the game does. It stays one command away — `locations.enabled 1` — because
// knowing which town you are looking at is exactly what it is good for while working on a map.
public static class NodesBuilder
{
    private const float LabelHeight = 28f; // metres above the node point, clearing most buildings and trees

    public static Node3D Build(string environmentDir)
    {
        var root = new Node3D { Name = "Locations", Visible = false };
        root.AddToGroup(SceneGroups.Locations);
        List<LocationNode> locations = LevelNodes.LoadLocations(Path.Combine(environmentDir, "Nodes.dat"));
        foreach (LocationNode loc in locations)
        {
            root.AddChild(new Label3D
            {
                Text = loc.Name,
                Position = loc.Position + (Vector3.Up * LabelHeight),
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                FixedSize = true, // constant on-screen size, so town names stay readable at any distance
                FontSize = 48,
                PixelSize = 0.0006f,
                OutlineSize = 16,
                OutlineModulate = new Color(0f, 0f, 0f, 0.8f),
                NoDepthTest = true, // readable through hills/buildings, like an on-map marker
                Name = $"Location_{loc.Name}",
            });
        }
        Log.Print($"[unturned-godot] Locations: {locations.Count}");
        return root;
    }
}
