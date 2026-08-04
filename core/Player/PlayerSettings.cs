using Godot;

namespace UnturnedGodot.Player;

// The player-configurable values Unturned reads from ControlsSettings / OptionsSettings (as opposed to the
// hardcoded physics constants in PlayerConfig). Defaults are Unturned's own defaults, so an untouched port
// matches a fresh install; a loader can later populate these from the game's settings files, keeping the
// controller data-driven rather than pinning magic numbers in the node.
public sealed class PlayerSettings
{
    // ControlsSettings.mouseAimSensitivity (degrees per mouse-axis unit). Default 0.2.
    public float MouseSensitivity { get; init; } = 0.2f;
    public bool InvertLook { get; init; }

    // OptionsSettings: fov slider 0.75 -> 60 + 40*0.75 = 90° vertical; sprint adds intensity*10 -> +10°.
    public float VerticalFovDegrees { get; init; } = 90f;
    public float SprintFovBoost { get; init; } = 10f;

    // ControlsSettings default key bindings.
    public Key Forward { get; init; } = Key.W;
    public Key Back { get; init; } = Key.S;
    public Key Left { get; init; } = Key.A;
    public Key Right { get; init; } = Key.D;
    public Key Jump { get; init; } = Key.Space;
    public Key Crouch { get; init; } = Key.X;
    public Key Prone { get; init; } = Key.Z;
    public Key Sprint { get; init; } = Key.Shift;
    public Key Perspective { get; init; } = Key.H;
    public Key Interact { get; init; } = Key.F;

    public static PlayerSettings Default { get; } = new();
}
