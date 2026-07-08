using Godot;
using UnturnedGodot.Data;

namespace UnturnedGodot;

// Builds the ocean: a single large translucent plane at the map's sea level. Unturned places the water
// surface at seaLevel * Level.TERRAIN, colored by the lighting's SEA color, which is what turns the
// sandy seabed into blue water and gives the island its coastline.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class WaterBuilder
{
    private const float TerrainHeight = 256f; // Level.TERRAIN: sea level is a fraction of this
    private const float PlaneSize = 16384f;   // spans the ~4 km map and out to the horizon

    // PEI's shipped sea level and blue midday sea color, used when a map ships no Lighting.dat.
    private const float DefaultSeaLevel = 0.1f;
    private static readonly Color DefaultSeaColor = new(0.482f, 0.608f, 0.792f);

    public static Node3D Build(LevelLighting? lighting)
    {
        float seaLevel = lighting?.SeaLevel ?? DefaultSeaLevel;
        Color seaColor = lighting?.Midday.Sea ?? DefaultSeaColor;

        var material = new StandardMaterial3D
        {
            AlbedoColor = new Color(seaColor.R, seaColor.G, seaColor.B, 0.88f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            // Read as the flat blue SEA color: a glossy surface just mirrors the grey sky and the sun.
            Roughness = 0.6f,
            Metallic = 0.0f,
            SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, // visible from above and from underwater
        };

        return new MeshInstance3D
        {
            Name = "Water",
            Mesh = new PlaneMesh { Size = new Vector2(PlaneSize, PlaneSize) },
            MaterialOverride = material,
            Position = new Vector3(0, seaLevel * TerrainHeight, 0),
        };
    }
}
