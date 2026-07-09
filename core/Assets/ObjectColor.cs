using Godot;

namespace UnturnedGodot.Assets;

// Placeholder tint per object type; unresolved GUIDs stay red so gaps in the asset db are visible.
public static class ObjectColor
{
    public static readonly Color Unresolved = new(0.9f, 0.2f, 0.2f);

    public static Color ForAsset(ObjectAsset? asset) => asset?.Type switch
    {
        EObjectType.Small => new Color(0.3f, 0.75f, 0.35f),
        EObjectType.Medium => new Color(0.3f, 0.55f, 0.9f),
        EObjectType.Large => new Color(0.95f, 0.6f, 0.2f),
        EObjectType.Npc => new Color(0.85f, 0.35f, 0.85f),
        EObjectType.Decal => new Color(0.6f, 0.6f, 0.6f),
        EObjectType.Resource => new Color(0.45f, 0.65f, 0.25f),
        EObjectType.Unknown => new Color(0.8f, 0.8f, 0.3f),
        _ => Unresolved, // null asset (unresolved GUID)
    };
}
