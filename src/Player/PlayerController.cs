using Godot;
using UnturnedGodot.Player;

namespace UnturnedGodot;

// A faithful port of Unturned's on-foot controller (PlayerMovement / PlayerLook / PlayerStance). The feel-
// defining maths and the numeric constants live in core/Player (PlayerConfig, PlayerMovement,
// PlayerStanceMachine) and are unit-tested against the game's real values; this node only samples input,
// resolves collision via CharacterBody3D, and drives the camera. Settings (sensitivity, FOV, control modes,
// key bindings) come from PlayerSettings, defaulting to Unturned's own defaults.
//
// Unturned simulates movement on a fixed 12.5 Hz tick for deterministic netcode; single-player here runs on
// Godot's physics step with the same instant-velocity/gravity/jump maths, which yields the same speeds,
// jump arc and gravity while staying smooth. Runtime keys: H = perspective, X = crouch, Z = prone,
// Shift = sprint, Space = jump, Esc = release mouse.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class PlayerController : CharacterBody3D
{
    private readonly PlayerSettings _settings = PlayerSettings.Default;

    // Set before adding to the tree to choose the initial perspective (e.g. third person for a screenshot).
    public bool StartThirdPerson { get; set; }

    // The third-person body model; when null a simple placeholder figure is used instead.
    public Node3D? BodyModel { get; set; }

    private Node3D _head = null!;
    private Camera3D _camera = null!;
    private CollisionShape3D _collider = null!;
    private CapsuleShape3D _capsule = null!;
    private Node3D _model = null!;

    private EPlayerStance _stance = EPlayerStance.Stand;
    private bool _wantCrouch;
    private bool _wantProne;
    private float _pitch;       // Godot pitch degrees: 0 = horizon, + up, - down
    private float _eyeHeight = PlayerConfig.EyeHeightStand;
    private bool _thirdPerson;

    public override void _Ready()
    {
        _thirdPerson = StartThirdPerson;

        FloorMaxAngle = Mathf.DegToRad(PlayerConfig.MaxWalkableSlopeDegrees);
        FloorSnapLength = 0.5f;
        FloorStopOnSlope = true;

        _capsule = new CapsuleShape3D { Radius = PlayerConfig.Radius, Height = PlayerConfig.HeightStand };
        _collider = new CollisionShape3D { Shape = _capsule, Position = Vector3.Up * (PlayerConfig.HeightStand * 0.5f) };
        AddChild(_collider);

        _model = BodyModel ?? BuildPlaceholderModel();
        AddChild(_model);

        _head = new Node3D { Position = Vector3.Up * _eyeHeight };
        AddChild(_head);
        _camera = new Camera3D { Fov = _settings.VerticalFovDegrees, Current = true, Name = "PlayerCamera" };
        _head.AddChild(_camera);
        ApplyPerspective();

        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            RotateY(-Mathf.DegToRad(motion.Relative.X * _settings.MouseSensitivity)); // yaw the whole body
            float dir = _settings.InvertLook ? 1f : -1f;
            (float down, float up) = PlayerConfig.PitchLimitsFor(_stance);
            _pitch = Mathf.Clamp(_pitch + (dir * motion.Relative.Y * _settings.MouseSensitivity), down, up);
            _head.RotationDegrees = new Vector3(_pitch, 0, 0);
            return;
        }

        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (key.Keycode == _settings.Perspective) { _thirdPerson = !_thirdPerson; ApplyPerspective(); }
            else if (key.Keycode == _settings.Crouch) { _wantCrouch = !_wantCrouch; if (_wantCrouch) _wantProne = false; }
            else if (key.Keycode == _settings.Prone) { _wantProne = !_wantProne; if (_wantProne) _wantCrouch = false; }
            else if (key.Keycode == Key.Escape)
                Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
                    ? Input.MouseModeEnum.Visible
                    : Input.MouseModeEnum.Captured;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;

        var input = new Vector2(
            (Input.IsKeyPressed(_settings.Right) ? 1f : 0f) - (Input.IsKeyPressed(_settings.Left) ? 1f : 0f),
            (Input.IsKeyPressed(_settings.Back) ? 1f : 0f) - (Input.IsKeyPressed(_settings.Forward) ? 1f : 0f));
        bool moving = input != Vector2.Zero;
        Vector3 wishDir = moving ? (Transform.Basis * new Vector3(input.X, 0f, input.Y)).Normalized() : Vector3.Zero;

        bool wantSprint = Input.IsKeyPressed(_settings.Sprint);
        UpdateStance(moving, wantSprint);

        float speed = PlayerConfig.SpeedFor(_stance);
        Vector3 velocity = Velocity;

        if (IsOnFloor())
        {
            Vector3 ground = PlayerMovement.GroundVelocity(wishDir, speed);
            velocity.X = ground.X;
            velocity.Z = ground.Z;
            bool canJump = Input.IsKeyPressed(_settings.Jump)
                && _stance is EPlayerStance.Stand or EPlayerStance.Sprint;
            velocity.Y = canJump ? PlayerConfig.JumpSpeed : -2f; // small downward keeps us snapped to the floor
        }
        else
        {
            velocity = PlayerMovement.AirVelocity(velocity, wishDir, speed, dt);
        }

        Velocity = velocity;
        MoveAndSlide();

        UpdateCamera(dt);
    }

    private void UpdateStance(bool moving, bool wantSprint)
    {
        EPlayerStance next = PlayerStanceMachine.Resolve(
            _stance, _wantCrouch, _wantProne, wantSprint, moving,
            hasStamina: true, // stamina/skills are a follow-up; base player is never exhausted
            canStand: HasClearance(PlayerConfig.HeightStand),
            canCrouch: HasClearance(PlayerConfig.HeightCrouch));
        if (next == _stance)
            return;

        _stance = next;
        float height = PlayerConfig.HeightFor(next);
        _capsule.Height = height;
        _collider.Position = Vector3.Up * (height * 0.5f);
    }

    // True when a standing/crouching capsule of the given height fits at the current position (headroom).
    private bool HasClearance(float targetHeight)
    {
        if (targetHeight <= _capsule.Height)
            return true; // dropping lower always fits

        var shape = new CapsuleShape3D { Radius = PlayerConfig.Radius - 0.01f, Height = targetHeight };
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = shape,
            Transform = new Transform3D(Basis.Identity, GlobalPosition + (Vector3.Up * (targetHeight * 0.5f))),
            CollisionMask = CollisionMask,
            Exclude = new Godot.Collections.Array<Rid> { GetRid() },
        };
        return GetWorld3D().DirectSpaceState.IntersectShape(query, 1).Count == 0;
    }

    private void UpdateCamera(float dt)
    {
        // Eye height eases toward the stance's value (rate 4), like PlayerLook's camera lerp.
        float targetEye = PlayerConfig.EyeHeightFor(_stance);
        _eyeHeight = Mathf.Lerp(_eyeHeight, targetEye, PlayerConfig.EyeLerpRate * dt);
        _head.Position = Vector3.Up * _eyeHeight;

        // Sprint widens the FOV a touch (rate 8).
        float targetFov = _settings.VerticalFovDegrees + (_stance == EPlayerStance.Sprint ? _settings.SprintFovBoost : 0f);
        _camera.Fov = Mathf.Lerp(_camera.Fov, targetFov, PlayerConfig.FovLerpRate * dt);

        if (_thirdPerson)
            PlaceThirdPersonCamera();
    }

    private void ApplyPerspective()
    {
        _model.Visible = _thirdPerson; // hide own body in first person
        if (_thirdPerson)
            PlaceThirdPersonCamera();
        else
            _camera.Position = Vector3.Zero;
    }

    // Over-the-shoulder third-person camera: ~2 m behind and up-right of the eye, pulled in on collision.
    private void PlaceThirdPersonCamera()
    {
        Vector3 local = new Vector3(PlayerConfig.ThirdPersonShoulder, PlayerConfig.ThirdPersonUp,
            PlayerConfig.ThirdPersonDistance); // +Z is behind (forward is -Z)
        Vector3 origin = _head.GlobalPosition;
        Vector3 target = _head.GlobalTransform * local;

        var ray = new PhysicsRayQueryParameters3D
        {
            From = origin,
            To = target,
            CollisionMask = CollisionMask,
            Exclude = new Godot.Collections.Array<Rid> { GetRid() },
        };
        Godot.Collections.Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(ray);
        if (hit.Count > 0)
        {
            var point = (Vector3)hit["position"];
            target = point + ((origin - point).Normalized() * PlayerConfig.CameraSweepRadius);
        }
        _camera.GlobalPosition = target;
    }

    // A simple stand-in figure used only when the real skinned body is unavailable.
    private static Node3D BuildPlaceholderModel()
    {
        var root = new Node3D { Name = "Model" };
        root.AddChild(new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Radius = PlayerConfig.Radius, Height = PlayerConfig.HeightStand },
            Position = Vector3.Up * (PlayerConfig.HeightStand * 0.5f),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.45f, 0.7f) },
            Name = "Body",
        });
        var head = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.18f, Height = 0.36f },
            Position = Vector3.Up * (PlayerConfig.HeightStand - 0.22f),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.8f, 0.65f, 0.5f) },
            Name = "Head",
        };
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
