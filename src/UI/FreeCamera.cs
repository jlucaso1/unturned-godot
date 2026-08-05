using Godot;

namespace UnturnedGodot;

public partial class FreeCamera : Camera3D
{
    [Export] public float Speed = 60f;
    [Export] public float BoostMultiplier = 6f;
    [Export] public float MouseSensitivity = 0.003f;

    private float _pitch;
    private float _yaw;
    private bool _captured;

    public override void _Ready()
    {
        Current = true;
        Far = 8000f; // map spans several km; avoid clipping distant terrain
        _pitch = Mathf.DegToRad(RotationDegrees.X);
        _yaw = Mathf.DegToRad(RotationDegrees.Y);
    }

    public override void _Input(InputEvent @event)
    {
        // Same rule as _Process below, and needed here for the other half of it: a click would otherwise
        // re-capture the mouse while the console is open, hiding the cursor over a pane you are trying
        // to click into. See TypingElsewhere.
        if (TypingElsewhere())
            return;
        if (@event is InputEventMouseButton { Pressed: true })
        {
            _captured = true;
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }
        else if (@event is InputEventKey { Keycode: Key.Escape, Pressed: true })
        {
            _captured = false;
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
        else if (@event is InputEventMouseMotion motion && _captured)
        {
            _yaw -= motion.Relative.X * MouseSensitivity;
            _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * MouseSensitivity,
                -Mathf.Pi / 2f + 0.01f, Mathf.Pi / 2f - 0.01f);
            Rotation = new Vector3(_pitch, _yaw, 0);
        }
    }

    // A focused text field owns the input. With the console open, WASD is somebody typing
    // `water.enabled 0`, not somebody flying — and this camera reads the keyboard by POLLING, so marking
    // the event handled cannot stop it the way it stops an event-driven listener. Asking the viewport who
    // holds focus is the engine's own answer to "is this keystroke text", and it covers every future text
    // field for free.
    private bool TypingElsewhere() => GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit;

    public override void _Process(double delta)
    {
        if (TypingElsewhere())
            return;

        // PHYSICAL keys, not keycodes. WASD is a shape — a cluster under the left hand — and asking for
        // the key that PRINTS "W" gives a different physical key on every layout that is not QWERTY: on
        // AZERTY that is where Z sits and A is where Q sits, so the camera answered to a scattering of
        // keys nobody would press. IsPhysicalKeyPressed names the position through its US-layout keycode,
        // which is the same cluster everywhere.
        //
        // PlayerController is deliberately not like this: it reads the player's own Unturned binds
        // (_settings.Forward and friends), so a keycode is the right question to ask there.

        var dir = Vector3.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W)) dir -= Transform.Basis.Z;
        if (Input.IsPhysicalKeyPressed(Key.S)) dir += Transform.Basis.Z;
        if (Input.IsPhysicalKeyPressed(Key.A)) dir -= Transform.Basis.X;
        if (Input.IsPhysicalKeyPressed(Key.D)) dir += Transform.Basis.X;
        if (Input.IsPhysicalKeyPressed(Key.E)) dir += Vector3.Up;
        if (Input.IsPhysicalKeyPressed(Key.Q)) dir += Vector3.Down;

        if (dir == Vector3.Zero)
            return; // idle: don't touch Position — writing it dirties the transform and re-sends the
                    // camera to the RenderingServer every frame even when the view hasn't moved.

        float speed = Speed * (Input.IsPhysicalKeyPressed(Key.Shift) ? BoostMultiplier : 1f);
        Position += dir.Normalized() * speed * (float)delta;
    }
}
