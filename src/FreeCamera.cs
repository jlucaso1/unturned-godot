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
        else if (@event is InputEventKey { Keycode: Key.F4, Pressed: true })
        {
            CopyShotCamToClipboard();
        }
        else if (@event is InputEventMouseMotion motion && _captured)
        {
            _yaw -= motion.Relative.X * MouseSensitivity;
            _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * MouseSensitivity,
                -Mathf.Pi / 2f + 0.01f, Mathf.Pi / 2f - 0.01f);
            Rotation = new Vector3(_pitch, _yaw, 0);
        }
    }

    public override void _Process(double delta)
    {
        var dir = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W)) dir -= Transform.Basis.Z;
        if (Input.IsKeyPressed(Key.S)) dir += Transform.Basis.Z;
        if (Input.IsKeyPressed(Key.A)) dir -= Transform.Basis.X;
        if (Input.IsKeyPressed(Key.D)) dir += Transform.Basis.X;
        if (Input.IsKeyPressed(Key.E)) dir += Vector3.Up;
        if (Input.IsKeyPressed(Key.Q)) dir += Vector3.Down;

        if (dir == Vector3.Zero)
            return; // idle: don't touch Position — writing it dirties the transform and re-sends the
                    // camera to the RenderingServer every frame even when the view hasn't moved.

        float speed = Speed * (Input.IsKeyPressed(Key.Shift) ? BoostMultiplier : 1f);
        Position += dir.Normalized() * speed * (float)delta;
    }

    // Copies the camera as a SHOT_CAM value ("px,py,pz,pitch,yaw") so a spot can be re-captured with
    // `SHOT_CAM=... godot --headless --resolution ...`. Invariant format keeps '.' decimals for the split.
    private void CopyShotCamToClipboard()
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        string shotCam = string.Format(c, "{0:0.##},{1:0.##},{2:0.##},{3:0.##},{4:0.##}",
            Position.X, Position.Y, Position.Z, RotationDegrees.X, RotationDegrees.Y);
        DisplayServer.ClipboardSet(shotCam);
        GD.Print($"[camera] copied SHOT_CAM={shotCam}");
    }
}
