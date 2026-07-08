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

        float speed = Speed * (Input.IsKeyPressed(Key.Shift) ? BoostMultiplier : 1f);
        Position += dir.Normalized() * speed * (float)delta;
    }
}
