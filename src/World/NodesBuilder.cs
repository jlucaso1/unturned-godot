using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Data;

namespace UnturnedGodot;

// Places a floating billboard label at each named location (town/landmark) from Environment/Nodes.dat,
// like Unturned's in-world place names. Labels show through terrain (map-marker style) so a town's name is
// readable even from across the island.
public static class NodesBuilder
{
    private const float LabelHeight = 28f; // metres above the node point, clearing most buildings and trees

    public static Node3D Build(string environmentDir)
    {
        var root = new Node3D { Name = "Locations" };
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
