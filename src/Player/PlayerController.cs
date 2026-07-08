using Godot;

namespace UnturnedGodot;

// A faithful-enough port of Unturned's character controller (PlayerMovement/PlayerLook/PlayerStance): a
// capsule character with walk/sprint/crouch speeds, a jump, mouse look with a clamped pitch, and a
// first/third-person camera. Numeric constants are Unturned's real values. Toggled against the free camera
// by a feature flag in Main; runtime keys: F5 = perspective, Ctrl = crouch, Esc = release the mouse.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class PlayerController : CharacterBody3D
{
    // PlayerMovement / PlayerStance constants (metres, m/s).
    private const float HeightStand = 2f;
    private const float HeightCrouch = 1.2f;
    private const float Radius = 0.4f;
    private const float SpeedStand = 4.5f;
    private const float SpeedSprint = 7f;
    private const float SpeedCrouch = 2.5f;
    private const float JumpSpeed = 7f;      // sqrt(2 * height * gravity); ~2.5 m jump
    private const float Gravity = 9.81f;     // LevelInfo default
    private const float EyeDrop = 0.2f;      // camera sits this far below the capsule top

    private const float MouseSensitivity = 0.12f; // degrees per pixel
    private const float Fov = 60f;                 // vertical FOV
    private const float SprintFovBoost = 8f;
    private const float ThirdPersonDistance = 4.5f;
    private const float ThirdPersonRight = 0.5f;
    private const float ThirdPersonUp = 0.3f;

    // Set before adding to the tree to choose the initial perspective (e.g. third person for a screenshot).
    public bool StartThirdPerson { get; set; }

    // The third-person body model; when null a simple placeholder figure is used instead.
    public Node3D? BodyModel { get; set; }

    private Node3D _head = null!;
    private Camera3D _camera = null!;
    private CollisionShape3D _collider = null!;
    private CapsuleShape3D _capsule = null!;
    private Node3D _model = null!;
    private float _pitch;
    private bool _thirdPerson;
    private bool _crouching;

    public override void _Ready()
    {
        _thirdPerson = StartThirdPerson;
        _capsule = new CapsuleShape3D { Radius = Radius, Height = HeightStand };
        _collider = new CollisionShape3D { Shape = _capsule, Position = new Vector3(0, HeightStand * 0.5f, 0) };
        AddChild(_collider);

        _model = BodyModel ?? BuildPlaceholderModel();
        AddChild(_model);

        _head = new Node3D { Position = new Vector3(0, HeightStand - EyeDrop, 0) };
        AddChild(_head);
        _camera = new Camera3D { Fov = Fov, Current = true, Name = "PlayerCamera" };
        _head.AddChild(_camera);
        ApplyPerspective();

        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            RotateY(-Mathf.DegToRad(motion.Relative.X * MouseSensitivity)); // yaw the whole body
            _pitch = Mathf.Clamp(_pitch - (motion.Relative.Y * MouseSensitivity), -89f, 89f);
            _head.RotationDegrees = new Vector3(_pitch, 0, 0);
            return;
        }

        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            switch (key.Keycode)
            {
                case Key.F5: _thirdPerson = !_thirdPerson; ApplyPerspective(); break;
                case Key.Ctrl: ToggleCrouch(); break;
                case Key.Escape:
                    Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
                        ? Input.MouseModeEnum.Visible
                        : Input.MouseModeEnum.Captured;
                    break;
            }
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        Vector3 velocity = Velocity;

        if (!IsOnFloor())
            velocity.Y -= Gravity * dt;
        else if (Input.IsKeyPressed(Key.Space) && !_crouching)
            velocity.Y = JumpSpeed;

        // WASD relative to where the body faces.
        var input = new Vector2(
            (Input.IsKeyPressed(Key.D) ? 1f : 0f) - (Input.IsKeyPressed(Key.A) ? 1f : 0f),
            (Input.IsKeyPressed(Key.S) ? 1f : 0f) - (Input.IsKeyPressed(Key.W) ? 1f : 0f));
        Vector3 direction = (Transform.Basis * new Vector3(input.X, 0f, input.Y)).Normalized();

        float speed = _crouching ? SpeedCrouch : (Input.IsKeyPressed(Key.Shift) ? SpeedSprint : SpeedStand);
        Vector3 desired = direction * speed;
        // Unturned pretends base acceleration equals the desired speed (~1 s to full speed).
        velocity.X = Mathf.MoveToward(velocity.X, desired.X, speed * dt);
        velocity.Z = Mathf.MoveToward(velocity.Z, desired.Z, speed * dt);

        Velocity = velocity;
        MoveAndSlide();

        // Sprint widens the FOV a touch, like Unturned.
        bool sprinting = speed == SpeedSprint && input != Vector2.Zero && IsOnFloor();
        _camera.Fov = Mathf.Lerp(_camera.Fov, sprinting ? Fov + SprintFovBoost : Fov, 8f * dt);
    }

    private void ToggleCrouch()
    {
        _crouching = !_crouching;
        float height = _crouching ? HeightCrouch : HeightStand;
        _capsule.Height = height;
        _collider.Position = new Vector3(0, height * 0.5f, 0);
        _model.Scale = new Vector3(1, height / HeightStand, 1);
        _head.Position = new Vector3(0, height - EyeDrop, 0);
    }

    private void ApplyPerspective()
    {
        _camera.Position = _thirdPerson
            ? new Vector3(ThirdPersonRight, ThirdPersonUp, ThirdPersonDistance)
            : Vector3.Zero;
        _model.Visible = _thirdPerson; // hide own body in first person
    }

    // A simple stand-in figure (real skinned character is a follow-up): a capsule body plus a head, so the
    // facing and stance read in third person. Children sit relative to the player's feet at the origin.
    private static Node3D BuildPlaceholderModel()
    {
        var root = new Node3D { Name = "Model" };
        root.AddChild(new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Radius = Radius, Height = HeightStand },
            Position = new Vector3(0, HeightStand * 0.5f, 0),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.45f, 0.7f) },
            Name = "Body",
        });

        var head = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.18f, Height = 0.36f },
            Position = new Vector3(0, HeightStand - 0.22f, 0),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.8f, 0.65f, 0.5f) },
            Name = "Head",
        };
        // A small nose (child of the head) marks which way the figure faces (-Z is forward in Godot).
        head.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.08f, 0.08f, 0.14f) },
            Position = new Vector3(0, 0, -0.18f),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.7f, 0.5f, 0.4f) },
            Name = "Nose",
        });
        root.AddChild(head);
        return root;
    }
}
