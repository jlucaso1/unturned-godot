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
// jump arc and gravity while staying smooth. Runtime keys: H (or F5) = perspective, X = crouch, Z = prone,
// Shift = sprint, Space = jump, Esc = release mouse.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class PlayerController : CharacterBody3D
{
    private readonly PlayerSettings _settings = PlayerSettings.Default;

    // Set before adding to the tree to choose the initial perspective (e.g. third person for a screenshot).
    public bool StartThirdPerson { get; set; }

    // The third-person body model; when null a simple placeholder figure is used instead.
    public Node3D? BodyModel { get; set; }

    // Movement audio (footsteps + landing), built by the caller with the map's terrain material data.
    public MovementAudio? Footsteps { get; set; }

    // Multiplayer session, when hosting or joined: the controller forwards inputs at the 12.5 Hz cadence.
    public UnturnedGodot.Net.NetClient? Net { get; set; }
    private double _netInputTimer;
    private uint _netFrame;

    // The camera this controller drives, for screen-space passes that must render in front of it.
    public Camera3D Camera => _camera;

    private Node3D _head = null!;
    private Camera3D _camera = null!;
    private CollisionShape3D _collider = null!;
    private CapsuleShape3D _capsule = null!;
    private Node3D _model = null!;
    private CharacterSkeleton? _rig; // the real body, when present, so stance changes repose it

    private EPlayerStance _stance = EPlayerStance.Stand;
    private bool _wantCrouch;
    private bool _wantProne;

    // Queued by the climb branch and spent on the tick the player leaves the ladder
    // (PlayerMovement.pendingLaunchVelocity).
    private Vector3 _pendingLaunchVelocity;
    private float _pitch;       // Godot pitch degrees: 0 = horizon, + up, - down
    private float _eyeHeight = PlayerConfig.EyeHeightStand;
    private bool _thirdPerson;
    private bool _benchmarkMovement;
    private ulong _benchmarkMovementStarted;
    private bool _worldReady;

    // ObjectStreamer calls this only after every terrain/object collider has joined the physics world.
    // Before then even an idle grounded player must keep integrating so newly attached geometry is seen.
    public void MarkWorldReady() => _worldReady = true;

    // Reused physics-query objects so the per-tick stance clearance test and the per-frame third-person
    // camera sweep don't allocate fresh RefCounted query/shape/exclude objects each call.
    private CapsuleShape3D? _clearanceShape;
    private PhysicsShapeQueryParameters3D? _clearanceQuery;
    private PhysicsRayQueryParameters3D? _cameraRay;
    private Godot.Collections.Array<Rid>? _selfExclude;

    // The climb probe runs every tick and the mount tests only on the tick a ladder is reached, so both
    // hold their query objects rather than allocating a RefCounted pair per tick.
    private PhysicsRayQueryParameters3D? _ladderRay;
    private PhysicsRayQueryParameters3D? _ladderLosRay;
    private PhysicsShapeQueryParameters3D? _ladderCapsule;
    private CapsuleShape3D? _ladderCapsuleShape;

    private Godot.Collections.Array<Rid> SelfExclude => _selfExclude ??= new Godot.Collections.Array<Rid> { GetRid() };

    public override void _Ready()
    {
        _thirdPerson = StartThirdPerson;
        _benchmarkMovement = EnvFlag.IsOn(OS.GetEnvironment("UG_RUNTIME_BENCH_MOVE"), whenUnset: false);
        _benchmarkMovementStarted = Engine.GetPhysicsFrames();

        // The body has its own bit so no world query treats the player as geometry. It used to sit on
        // bit 1, which is VisionBlocker — the bit the zombie alert raycast masks — so that ray ended
        // inside the player's own capsule at close range and reported vision blocked.
        CollisionLayer = CollisionLayers.Player;
        CollisionMask = CollisionLayers.CharacterMask; // world + furniture

        FloorMaxAngle = Mathf.DegToRad(PlayerConfig.MaxWalkableSlopeDegrees);
        FloorSnapLength = 0.5f;
        FloorStopOnSlope = true;

        _capsule = new CapsuleShape3D { Radius = PlayerConfig.Radius, Height = PlayerConfig.HeightStand };
        _collider = new CollisionShape3D { Shape = _capsule, Position = Vector3.Up * (PlayerConfig.HeightStand * 0.5f) };
        AddChild(_collider);

        _model = BodyModel ?? BuildPlaceholderModel();
        _rig = BodyModel as CharacterSkeleton;
        AddChild(_model);

        _head = new Node3D { Position = Vector3.Up * _eyeHeight };
        AddChild(_head);
        _camera = new Camera3D { Fov = _settings.VerticalFovDegrees, Current = true, Name = "PlayerCamera" };
        _head.AddChild(_camera);
        ApplyPerspective();

        AddChild(BuildClimbPrompt());

        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    // EPlayerMessage.CLIMB: the prompt Unturned puts under the crosshair when a ladder is in reach. It is
    // the only interaction hint this port has, so it brings its own layer rather than assuming a HUD.
    private CanvasLayer BuildClimbPrompt()
    {
        var layer = new CanvasLayer { Name = "ClimbPrompt" };
        _climbPrompt = new Label
        {
            Text = $"Climb [{OS.GetKeycodeString(_settings.Interact)}]",
            Visible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            AnchorLeft = 0f,
            AnchorRight = 1f,
            AnchorTop = 0.5f,
            AnchorBottom = 0.5f,
            OffsetTop = 48f,
            OffsetBottom = 72f,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _climbPrompt.AddThemeFontSizeOverride("font_size", 18);
        _climbPrompt.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.7f));
        _climbPrompt.AddThemeConstantOverride("outline_size", 6);
        layer.AddChild(_climbPrompt);
        return layer;
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

        if (@event is InputEventKey { Pressed: true, Echo: false } key
            && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            if (key.Keycode == _settings.Perspective || key.Keycode == Key.F5) { _thirdPerson = !_thirdPerson; ApplyPerspective(); }
            else if (key.Keycode == _settings.Crouch) { _wantCrouch = !_wantCrouch; if (_wantCrouch) _wantProne = false; }
            else if (key.Keycode == _settings.Prone) { _wantProne = !_wantProne; if (_wantProne) _wantCrouch = false; }
            // Escape belongs to PauseMenu, which owns mouse capture.
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        long benchmarkStarted = Benchmark.RuntimeCounters.Start();
        float dt = (float)delta;

        // Mouse released = a menu owns the input (PauseMenu): freeze movement keys; physics still runs.
        bool inputCaptured = Input.MouseMode == Input.MouseModeEnum.Captured;
        var input = _benchmarkMovement
            // Alternate forward/back every second. This keeps the player near the deterministic spawn
            // while exercising real MoveAndSlide, step-up and the 12.5 Hz loopback position stream.
            ? new Vector2(0f, ((Engine.GetPhysicsFrames() - _benchmarkMovementStarted) / 60) % 2 == 0 ? -1f : 1f)
            : inputCaptured
            ? new Vector2(
                (Input.IsKeyPressed(_settings.Right) ? 1f : 0f) - (Input.IsKeyPressed(_settings.Left) ? 1f : 0f),
                (Input.IsKeyPressed(_settings.Back) ? 1f : 0f) - (Input.IsKeyPressed(_settings.Forward) ? 1f : 0f))
            : Vector2.Zero;
        bool moving = input != Vector2.Zero;
        Vector3 wishDir = moving ? (Transform.Basis * new Vector3(input.X, 0f, input.Y)).Normalized() : Vector3.Zero;

        bool wantSprint = inputCaptured && Input.IsKeyPressed(_settings.Sprint);

        // Ladders are resolved BEFORE the stance intents, like PlayerStance.simulate: while a ladder is
        // held, the whole crouch/prone/sprint block is skipped, and the intents themselves are cleared so
        // a toggle pressed mid-climb does not take effect the moment the player steps off.
        ClimbDecision climb = UpdateLadder();
        // The interact mount is offered only to a player who is not already on a ladder, and taken only
        // on the press — then the probe above mounts them on the next tick, as the game's does.
        UpdateClimbPrompt(inputCaptured && !climb.IsClimbing, dt);
        if (TryInteractClimb(inputCaptured && Input.IsKeyPressed(_settings.Interact)))
        {
            // The mount turned us to face the ladder, so this tick's movement belongs to the new facing —
            // otherwise the first step after interacting goes wherever the player happened to be walking.
            wishDir = moving ? (Transform.Basis * new Vector3(input.X, 0f, input.Y)).Normalized() : Vector3.Zero;
        }
        bool stanceChanged;
        if (climb.IsClimbing)
        {
            _wantCrouch = false;
            _wantProne = false;
            stanceChanged = climb.Transition == EClimbTransition.Mount;
        }
        else
        {
            stanceChanged = UpdateStance(moving, wantSprint);
            stanceChanged |= climb.Transition == EClimbTransition.Dismount;
        }
        bool climbing = _stance == EPlayerStance.Climb;

        _rig?.SetState(_stance, moving); // crossfades to Idle_/Move_<stance>
        _rig?.SetPitch(_pitch);          // bends the upper body toward the look

        float speed = PlayerConfig.SpeedFor(_stance);
        bool wasOnFloor = IsOnFloor();
        bool canJump = wasOnFloor && !climbing && inputCaptured && Input.IsKeyPressed(_settings.Jump)
            && _stance is EPlayerStance.Stand or EPlayerStance.Sprint;
        bool integrate = climbing || PlayerPhysicsActivity.NeedsIntegration(
            _worldReady, wasOnFloor, moving, canJump, stanceChanged);
        Vector3 velocity = Velocity;
        if (climbing)
        {
            // PlayerMovement.simulate's CLIMB branch: vertical only, at half the climb speed, with no
            // gravity — and a forward nudge queued for the tick the ladder ends, so stepping off the top
            // reaches the surface in front instead of dropping back down the shaft.
            float moveZ = -input.Y; // Unturned's convention: +1 is forward
            _pendingLaunchVelocity = PlayerLadder.LaunchVelocity(GlobalTransform.Basis, moveZ);
            velocity = PlayerLadder.ClimbVelocity(moveZ, speed);
        }
        else if (wasOnFloor)
        {
            Vector3 ground = PlayerMovement.GroundVelocity(wishDir, speed);
            velocity.X = ground.X;
            velocity.Z = ground.Z;
            velocity.Y = canJump ? PlayerConfig.JumpSpeed : -2f; // small downward keeps us snapped to the floor
            velocity += _pendingLaunchVelocity;
            _pendingLaunchVelocity = Vector3.Zero;
        }
        else
        {
            velocity = PlayerMovement.AirVelocity(velocity, wishDir, speed, dt);
            velocity += _pendingLaunchVelocity;
            _pendingLaunchVelocity = Vector3.Zero;
        }

        bool isOnFloor = wasOnFloor;
        if (integrate)
        {
            Velocity = velocity;
            Vector3 beforeMove = GlobalPosition;
            long moveStarted = Benchmark.RuntimeCounters.Start();
            MoveAndSlide();
            Benchmark.RuntimeCounters.Record(Benchmark.RuntimeCounters.Counter.PlayerMoveAndSlide, moveStarted);
            long stepStarted = Benchmark.RuntimeCounters.Start();
            PlayerStep.TryStepUp(this, beforeMove, new Vector3(velocity.X, 0f, velocity.Z) * dt);
            Benchmark.RuntimeCounters.Record(Benchmark.RuntimeCounters.Counter.PlayerStep, stepStarted);
            isOnFloor = IsOnFloor();
        }

        // checkGround forces a climber grounded: they are on a ladder, not falling, and the footstep
        // clock ticks their rungs rather than thudding a landing when they reach the top.
        isOnFloor |= climbing;

        // PlayerMovement's footstep block: the MovementSoundClock derives the landing thud on the
        // airborne->grounded edge and the 2.1/speed footstep cadence from this state, like every client
        // does for every player (local and remote) — movement audio never travels over the network.
        Footsteps?.Tick(_stance, moving, isOnFloor, GlobalPosition, dt);

        // Multiplayer: forward one input frame per 0.08 s (PlayerInput.RATE). Idle frames still flow so
        // the server keeps simulating (gravity) and other players see us stop.
        if (Net != null)
        {
            _netInputTimer += dt;
            if (_netInputTimer >= UnturnedGodot.Net.ServerSimulation.TickRate)
            {
                _netInputTimer -= UnturnedGodot.Net.ServerSimulation.TickRate;
                bool jumpHeld = inputCaptured && Input.IsKeyPressed(_settings.Jump);
                // Trusted-client frame: our position already resolved collision against the full world
                // (objects, buildings) that the server's heightfield solver doesn't know about.
                Net.SendInput(new UnturnedGodot.Net.InputCommand(_netFrame++,
                    (sbyte)input.X, (sbyte)input.Y, jumpHeld, wantSprint,
                    UnturnedGodot.Net.NetAngles.QuantizeYaw(RotationDegrees.Y),
                    UnturnedGodot.Net.NetAngles.QuantizePitch(_pitch + 90f),
                    _stance, GlobalPosition, isOnFloor));
            }
        }

        UpdateCamera(dt);
        Benchmark.RuntimeCounters.Record(Benchmark.RuntimeCounters.Counter.PlayerPhysics, benchmarkStarted);
    }

    // One tick of PlayerStance.simulate's ladder block: probe forward, mount what the probe found, hold
    // the climb while it keeps finding it, and stand back up the moment it does not.
    private ClimbDecision UpdateLadder()
    {
        if (_stance != EPlayerStance.Climb && !PlayerLadder.CanTransitionToClimbing(_stance))
            return default;

        LadderContact? contact = ProbeLadder(PlayerLadder.ProbeOrigin(GlobalPosition),
            -GlobalTransform.Basis.Z, PlayerConfig.LadderProbeRange);
        ClimbDecision decision = PlayerLadder.Resolve(_stance, GlobalPosition, contact,
            IsLadderPathBlocked, IsCapsuleOccupied);

        switch (decision.Transition)
        {
            case EClimbTransition.Mount:
                GlobalPosition = decision.MountPoint;
                SetStance(EPlayerStance.Climb);
                break;
            case EClimbTransition.Dismount:
                SetStance(EPlayerStance.Stand);
                break;
        }

        return decision;
    }

    // --- The interact path: InteractableLadder's "Climb" prompt and PlayerStance.ReceiveClimbRequest ---

    private Label? _climbPrompt;
    private double _sinceInteractProbe = InteractProbeInterval;
    private bool _canInteractClimb;
    private Vector3 _interactMountPoint;
    private float _interactYawDegrees;
    private bool _interactHeld;

    // PlayerInteract re-casts its focus ray at this cadence rather than every frame.
    private const double InteractProbeInterval = 0.1;

    // A ladder within reach of the look ray can be mounted outright, which is how the game lets a player
    // climb onto one they cannot walk into — the top of a ladder over a ledge, most of all. The rules are
    // stricter than walking into it: the reach is short, angled ladders are refused, and the capsule the
    // mount would create has to both fit and be visible from the hit.
    private void UpdateClimbPrompt(bool eligible, double dt)
    {
        _sinceInteractProbe += dt;
        if (!eligible)
        {
            // Mounting a ladder, or opening a menu, takes the offer away now rather than at the next probe.
            _canInteractClimb = false;
        }
        else if (_sinceInteractProbe >= InteractProbeInterval)
        {
            _sinceInteractProbe = 0;
            _canInteractClimb = PlayerLadder.CanInteractClimb(_stance,
                ProbeLadder(_head.GlobalPosition, -_head.GlobalTransform.Basis.Z,
                    PlayerConfig.LadderInteractRange),
                HasLineOfSightToMount, HasInteractMountClearance,
                out _interactMountPoint, out _interactYawDegrees);
        }

        if (_climbPrompt != null)
            _climbPrompt.Visible = _canInteractClimb;
    }

    private bool HasLineOfSightToMount(Vector3 from, Vector3 capsuleCentre)
    {
        _ladderLosRay ??= new PhysicsRayQueryParameters3D
        {
            CollisionMask = CollisionLayers.BlockLadderMask,
            Exclude = SelfExclude,
        };
        _ladderLosRay.From = from;
        _ladderLosRay.To = capsuleCentre;
        return GetWorld3D().DirectSpaceState.IntersectRay(_ladderLosRay).Count == 0;
    }

    private bool HasInteractMountClearance(Vector3 feet) =>
        HasCapsuleClearance(feet, PlayerLadder.InteractTestHeight);

    // The teleport itself, once the key is pressed: land the validated mount point facing the ladder. The
    // stance stays as it was — the probe above mounts the ladder on the next tick, exactly as the game's
    // does after its own climb request lands.
    private bool TryInteractClimb(bool held)
    {
        bool pressed = held && !_interactHeld;
        _interactHeld = held;
        if (!pressed || !_canInteractClimb)
            return false;

        GlobalPosition = PlayerLadder.TeleportDestination(_interactMountPoint);
        RotationDegrees = new Vector3(0f, _interactYawDegrees, 0f);
        Velocity = Vector3.Zero;          // Player.ReceiveTeleport -> updateMovement clears both
        _pendingLaunchVelocity = Vector3.Zero;
        _canInteractClimb = false;
        if (_climbPrompt != null)
            _climbPrompt.Visible = false;
        return true;
    }

    // The first thing a ray meets on RayMasks.LADDER_INTERACT, as a ladder contact — null when that first
    // thing is not a ladder, which is what stops a player mounting one through a wall.
    private LadderContact? ProbeLadder(Vector3 from, Vector3 direction, float range)
    {
        _ladderRay ??= new PhysicsRayQueryParameters3D
        {
            CollisionMask = CollisionLayers.LadderInteractMask,
            Exclude = SelfExclude,
        };
        _ladderRay.From = from;
        _ladderRay.To = from + (direction * range);
        Godot.Collections.Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(_ladderRay);
        return LadderVolumes.TryResolve(hit, out LadderContact contact) ? contact : null;
    }

    // The capsule the ladder tests are made of, at whatever height the test wants (the interact path asks
    // for a taller one, since its teleport lifts the player). Its radius is a hair under the real one so a
    // capsule resting against the geometry it is about to climb does not read as inside it — the same trim
    // HasClearance uses, and the same reason the game's own clearance test lifts its capsule 1 cm.
    //
    // RayMasks.BLOCK_LADDER and RayMasks.BLOCK_STANCE list the same layers, and both reduce to the solid
    // world plus furniture here, so one mask serves the sweep, the destination and the line of sight.
    private PhysicsShapeQueryParameters3D LadderCapsuleQuery(Vector3 feet, float height)
    {
        _ladderCapsuleShape ??= new CapsuleShape3D { Radius = PlayerConfig.Radius - 0.01f };
        _ladderCapsule ??= new PhysicsShapeQueryParameters3D
        {
            Shape = _ladderCapsuleShape,
            CollisionMask = CollisionLayers.BlockLadderMask,
            Exclude = SelfExclude,
        };
        _ladderCapsuleShape.Height = height;
        _ladderCapsule.Transform = new Transform3D(Basis.Identity, feet + (Vector3.Up * (height * 0.5f)));
        _ladderCapsule.Motion = Vector3.Zero;
        return _ladderCapsule;
    }

    // Physics.CapsuleCast against RayMasks.BLOCK_LADDER: is there anything solid between the player and
    // the ladder's face? Godot reports [0, 0] when the capsule is already overlapping something at the
    // start, which is exactly the case Unity's CapsuleCast cannot see either — and exactly why the game
    // follows it with the destination test below rather than trusting the sweep alone.
    private bool IsLadderPathBlocked(Vector3 from, Vector3 to)
    {
        PhysicsShapeQueryParameters3D query = LadderCapsuleQuery(from, PlayerConfig.HeightStand);
        query.Motion = to - from;
        float[] fractions = GetWorld3D().DirectSpaceState.CastMotion(query);
        query.Motion = Vector3.Zero;
        if (fractions.Length < 2 || fractions[0] >= 1f)
            return false;
        return fractions[0] > 0f || fractions[1] > 0f;
    }

    // Physics.CheckCapsule at the destination: a standing capsule there would already be inside something.
    private bool IsCapsuleOccupied(Vector3 feet) => !HasCapsuleClearance(feet, PlayerConfig.HeightStand);

    // PlayerStance.hasHeightClearanceAtPosition, for a capsule of the given height standing at `feet`.
    private bool HasCapsuleClearance(Vector3 feet, float height) =>
        GetWorld3D().DirectSpaceState
            .IntersectShape(LadderCapsuleQuery(feet, height), 1).Count == 0;

    private bool UpdateStance(bool moving, bool wantSprint)
    {
        // Shape intersection is a real physics query. Most ticks do not attempt to raise the capsule, so
        // do not eagerly test both standing and crouching headroom merely to pass booleans to the pure
        // state machine.
        bool canStand = !PlayerStanceMachine.NeedsStandClearance(_stance, _wantCrouch, _wantProne)
            || HasClearance(PlayerConfig.HeightStand);
        bool canCrouch = !PlayerStanceMachine.NeedsCrouchClearance(_stance, _wantCrouch)
            || HasClearance(PlayerConfig.HeightCrouch);
        EPlayerStance next = PlayerStanceMachine.Resolve(
            _stance, _wantCrouch, _wantProne, wantSprint, moving,
            hasStamina: true, // stamina/skills are a follow-up; base player is never exhausted
            canStand, canCrouch);
        return SetStance(next);
    }

    // PlayerStance.internalSetStance: the capsule follows the stance (PlayerMovement.setSize). Returns
    // whether anything changed, which is what decides if this tick has to integrate collision at all.
    private bool SetStance(EPlayerStance next)
    {
        if (next == _stance)
            return false;

        _stance = next;
        float height = PlayerConfig.HeightFor(next);
        _capsule.Height = height;
        _collider.Position = Vector3.Up * (height * 0.5f);
        return true;
    }

    // True when a standing/crouching capsule of the given height fits at the current position (headroom).
    private bool HasClearance(float targetHeight)
    {
        if (targetHeight <= _capsule.Height)
            return true; // dropping lower always fits

        if (_clearanceQuery == null)
        {
            _clearanceShape = new CapsuleShape3D { Radius = PlayerConfig.Radius - 0.01f };
            _clearanceQuery = new PhysicsShapeQueryParameters3D
            {
                Shape = _clearanceShape,
                CollisionMask = CollisionMask,
                Exclude = SelfExclude,
            };
        }
        _clearanceShape!.Height = targetHeight;
        _clearanceQuery.Transform = new Transform3D(Basis.Identity, GlobalPosition + (Vector3.Up * (targetHeight * 0.5f)));
        return GetWorld3D().DirectSpaceState.IntersectShape(_clearanceQuery, 1).Count == 0;
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
        // The third-person collision ray belongs to UpdateCamera in _PhysicsProcess. _Ready and _Input
        // both call this method outside a physics notification, where separate-threaded physics can have
        // its direct space locked. The next physics tick places a newly enabled third-person camera.
        if (!_thirdPerson)
            _camera.Position = Vector3.Zero;
    }

    // Over-the-shoulder third-person camera: ~2 m behind and up-right of the eye, pulled in on collision.
    private void PlaceThirdPersonCamera()
    {
        Vector3 local = new Vector3(PlayerConfig.ThirdPersonShoulder, PlayerConfig.ThirdPersonUp,
            PlayerConfig.ThirdPersonDistance); // +Z is behind (forward is -Z)
        Vector3 origin = _head.GlobalPosition;
        Vector3 target = _head.GlobalTransform * local;

        _cameraRay ??= new PhysicsRayQueryParameters3D { CollisionMask = CollisionMask, Exclude = SelfExclude };
        _cameraRay.From = origin;
        _cameraRay.To = target;
        Godot.Collections.Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(_cameraRay);
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
