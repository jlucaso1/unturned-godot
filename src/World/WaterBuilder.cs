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

    public static Node3D Build(LevelLighting? lighting, out StandardMaterial3D material)
    {
        float seaLevel = lighting?.SeaLevel ?? DefaultSeaLevel;
        Color seaColor = lighting?.Midday.Sea ?? DefaultSeaColor;

        material = new StandardMaterial3D
        {
            AlbedoColor = new Color(seaColor.R, seaColor.G, seaColor.B, 0.88f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            // Read as the flat blue SEA color: a glossy surface just mirrors the grey sky and the sun.
            Roughness = 0.6f,
            Metallic = 0.0f,
            SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, // visible from above and from underwater
            // Shadows fall through onto the seabed, not onto the surface — Unturned's translucent water
            // doesn't catch shadows, and a tree shadow floating on the sea reads wrong.
            DisableReceiveShadows = true,
            // Write depth even though the surface alpha-blends: foliage chunks in their VisibilityRange
            // dither-fade temporarily render through the transparent pipeline, and per-object sorting
            // against this map-sized plane is ambiguous — underwater pebbles in fade drew ON TOP of the
            // sea. With the surface in the depth buffer, submerged fading geometry fails the depth test in
            // either draw order, while foliage on land (in front of the water) still fades normally.
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always,
        };

        return new MeshInstance3D
        {
            Name = "Water",
            Mesh = new PlaneMesh { Size = new Vector2(PlaneSize, PlaneSize) },
            MaterialOverride = material,
            Position = new Vector3(0, seaLevel * TerrainHeight, 0),
            // Transparent caster: it produces a bogus opaque shadow on the seabed and is re-drawn into
            // every directional cascade for no visible benefit. Don't cast.
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }
}
