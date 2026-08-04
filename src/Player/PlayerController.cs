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

    // The first-person arms (the prefab's Viewmodel rig). Optional: without it first person simply shows
    // no hands, which is what the port did before.
    public Node3D? ViewmodelModel { get; set; }

    // Movement audio (footsteps + landing), built by the caller with the map's terrain material data.
    public MovementAudio? Footsteps { get; set; }

    // The shared positional voice pool, for the sounds a gesture makes. The owner plays its own swing
    // straight away for the same reason it animates it straight away: the sound belongs to the frame the
    // button went down, not to the round trip.
    public OneShotAudio? Sounds { get; set; }

    // Multiplayer session, when hosting or joined: the controller forwards inputs at the 12.5 Hz cadence.
    public UnturnedGodot.Net.NetClient? Net { get; set; }

    // The camera this controller drives, for screen-space passes that must render in front of it.
    public Camera3D Camera => _camera;

    private Node3D _head = null!;
    private Camera3D _camera = null!;
    private CollisionShape3D _collider = null!;
    private CapsuleShape3D _capsule = null!;
    private Node3D _model = null!;
    private CharacterSkeleton? _rig; // the real body, when present, so stance changes repose it
    private CharacterSkeleton? _viewmodel; // the first-person arms, posed by the same clips as the body

    // The hands. Everything the player does with them — punching today, a swung machete or a fired gun
    // later — is decided here, on the same 12.5 Hz tick the server re-runs it on.
    private readonly UnturnedGodot.Player.PlayerEquipment _equipment = new();

    // The 12.5 Hz simulation counter. ONE counter, not one for the hands and another for the wire: it is
    // both the frame number the server orders our inputs by and the tick PlayerEquipment measures its
    // cooldown in, and two of them would part company the moment a session attached mid-run.
    private uint _tick;

    // The tick is scheduled against elapsed time rather than drained from an accumulator. An accumulator
    // that subtracts one interval per physics frame runs the tick at the FRAME rate while it works off
    // whatever a hitch left in it — six ticks inside a tenth of a second — and the hands count their
    // cooldown in ticks, so the owner would accept and announce a swing the server's real-time rule then
    // refuses. Dropping that backlog instead is the opposite error: the counter would then lose the
    // seconds a stall ate and refuse a swing whose cooldown had really expired. The tick therefore
    // advances by the intervals that genuinely passed, which is the only reading of it that stays a
    // measure of real time in both directions. See the tick block in _PhysicsProcess.
    private double _elapsed;
    private double _nextTickAt = UnturnedGodot.Net.ServerSimulation.TickRate;
    private EAttackInputFlags _primaryPending;
    private EAttackInputFlags _secondaryPending;

    // The swing being announced, and how many more frames it is announced on. One number per accepted
    // swing, repeated verbatim: that is what lets the server tell a retransmission from a second punch.
    private byte _swingSequence;
    private EPlayerPunch _swingFist;
    private int _swingRepeats;

    // How many input frames a thrown swing is announced on. Input datagrams are unreliable, so a single
    // dropped one would eat the swing for everyone else while the thrower saw their own — the one desync
    // a locally-predicted action can produce. Repeating is safe because every copy carries the same
    // number: the server answers the first it receives and recognises the rest as that same swing, so
    // which of them survives the trip changes nothing about what anyone sees.
    private const int AttackEdgeRepeats = 2;

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
        AttachViewmodel();
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
            ApplyPitch(_pitch + (dir * motion.Relative.Y * _settings.MouseSensitivity));
            return;
        }

        // Attack input is latched as an EDGE, not sampled as held state: PlayerEquipment reacts to the
        // tick a button went down, and polling it on the physics step would miss a click that started and
        // ended inside one 0.08 s tick.
        if (@event is InputEventMouseButton { Pressed: true } button
            && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            // The repeat counter is NOT armed here: it repeats a swing, and whether this press becomes
            // one is the next tick's decision.
            if (button.ButtonIndex == _settings.AttackPrimary)
                _primaryPending |= EAttackInputFlags.Start;
            // The secondary button is deliberately not bound yet: with an item equipped it aims rather
            // than punches, and binding it to a right-hand swing now would have to be taken back.
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
        // The arms follow the same movement state so they idle and bob like the body; the camera already
        // carries the look, so they take no pitch bend of their own.
        _viewmodel?.SetState(_stance, moving);

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

        // The 12.5 Hz simulation tick (PlayerInput.RATE). It runs whether or not a session is attached,
        // because the hands are simulated on it too — a punch has to work the same in a local world as in
        // a joined one. Idle frames still flow to the server so it keeps simulating (gravity) and other
        // players see us stop.
        _elapsed += dt;
        if (_elapsed >= _nextTickAt)
        {
            // The counter advances by however many intervals of real time have ACTUALLY elapsed — one on
            // an ordinary frame, several at once after a hitch. The hands measure their cooldown in it,
            // so this is what keeps that cooldown measuring real seconds from both directions: it never
            // runs at the frame rate while a backlog drains, and it never swallows the seconds a stall
            // ate and leaves the owner unable to punch for a cooldown that has already expired.
            uint intervals = 1 + (uint)((_elapsed - _nextTickAt) / UnturnedGodot.Net.ServerSimulation.TickRate);
            _tick += intervals;
            _nextTickAt += intervals * UnturnedGodot.Net.ServerSimulation.TickRate;
            // The hands run on the fresh press, once. What goes on the wire afterwards is the swing they
            // threw, announced under a number of its own for a few frames; see AttackEdgeRepeats.
            EAttackInputFlags primary = _primaryPending;
            EAttackInputFlags secondary = _secondaryPending;
            _primaryPending = EAttackInputFlags.None;
            _secondaryPending = EAttackInputFlags.None;

            EPlayerGesture gesture = SimulateHands(primary, secondary);
            if (gesture is EPlayerGesture.PunchLeft or EPlayerGesture.PunchRight)
            {
                // A new number is what makes this a new swing to everyone downstream. It is minted only
                // when the hands ACCEPTED one, so a press the cooldown or the prone gate refused never
                // reaches the wire at all.
                _swingSequence = (byte)((_swingSequence + 1) & 0x7F);
                _swingFist = gesture == EPlayerGesture.PunchRight ? EPlayerPunch.Right : EPlayerPunch.Left;
                _swingRepeats = AttackEdgeRepeats;
            }

            bool sendSwing = _swingRepeats > 0;
            if (sendSwing)
                _swingRepeats--;

            if (Net != null)
            {
                bool jumpHeld = inputCaptured && Input.IsKeyPressed(_settings.Jump);
                // Trusted-client frame: our position already resolved collision against the full world
                // (objects, buildings) that the server's heightfield solver doesn't know about. The
                // attack flags are NOT trusted the same way — the server re-runs the same rule on them
                // and is what tells everyone else a punch happened.
                Net.SendInput(new UnturnedGodot.Net.InputCommand(_tick,
                    (sbyte)input.X, (sbyte)input.Y, jumpHeld, wantSprint,
                    UnturnedGodot.Net.NetAngles.QuantizeYaw(RotationDegrees.Y),
                    UnturnedGodot.Net.NetAngles.QuantizePitch(_pitch + 90f),
                    _stance, GlobalPosition, isOnFloor, sendSwing, _swingSequence, _swingFist));
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

    // Runs the hands for one simulation tick and plays whatever they did. The owner acts on its own
    // decision immediately rather than waiting for the server to confirm it, exactly as PlayerEquipment
    // does — the swing has to start on the frame the button went down, and the server's copy of the same
    // rule reaches the other players a round trip later.
    private EPlayerGesture SimulateHands(EAttackInputFlags primary, EAttackInputFlags secondary)
    {
        if (primary == EAttackInputFlags.None && secondary == EAttackInputFlags.None)
            return EPlayerGesture.None;

        EPlayerGesture gesture = _equipment.Simulate(_tick, primary, secondary,
            new HandState { Stance = _stance });

        // Chest height, matching what the other clients play for this same swing off the replicated
        // gesture — the owner just does not wait for the round trip to hear it.
        if (PlayerGestures.SoundFor(gesture) is { } sound)
            Sounds?.Play(sound, GlobalPosition + Vector3.Up,
                PlayerGestures.PunchVolume, PlayerGestures.PunchMaxDistance);

        if (PlayerGestures.ClipFor(gesture) is not { } clip)
            return gesture;

        DrawnRig?.PlayOnce(clip);
        return gesture;
    }

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
        // The new stance may not allow the view the old one did — mounting a ladder while looking
        // straight down is the sharpest case, since a climber may not look below 10 degrees at all.
        ApplyPitch(_pitch);
        return true;
    }

    // PlayerLook's per-frame pitch clamp, against whatever stance is current.
    private void ApplyPitch(float pitchDegrees)
    {
        _pitch = PlayerConfig.ClampPitch(pitchDegrees, _stance);
        _head.RotationDegrees = new Vector3(_pitch, 0, 0);
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

    // Hangs the first-person arms off the head so they follow the look, and slides the rig so its Skull
    // bone lands on the camera — which is exactly where Unturned parents its ViewmodelCamera. Deriving the
    // offset from the rig's own rest pose keeps it correct if the character is ever re-authored, instead
    // of pinning a measured number. UG_VIEWMODEL_OFFSET="x,y,z" nudges it from there.
    private void AttachViewmodel()
    {
        if (ViewmodelModel is { } imported && imported is not CharacterSkeleton)
        {
            // The import succeeded but produced a static bind-pose mesh rather than a rig — the arms
            // decoded, their skinning did not. It cannot be posed, so it would hang in front of the
            // camera in one frozen attitude and never throw the punch it is there for. The body can be
            // posed, so first person is better off with that; this is freed rather than left parentless,
            // which is a Godot node nothing owns and nothing frees for the rest of the session.
            imported.QueueFree();
            ViewmodelModel = null;
        }

        if (ViewmodelModel is not CharacterSkeleton rig)
            return;

        _viewmodel = rig;
        int skull = rig.FindBone("Skull");
        Vector3 offset = skull >= 0 ? -rig.GetBoneGlobalRest(skull).Origin : Vector3.Zero;
        rig.Position = offset + EnvOffset("UG_VIEWMODEL_OFFSET");
        // Close to the near plane and lit like the world around it, but never casting into it: an arm a
        // handspan from the eye throws a shadow across the whole view.
        foreach (Node child in rig.GetChildren())
            if (child is GeometryInstance3D geometry)
                geometry.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        _camera.AddChild(rig);
    }

    private static Vector3 EnvOffset(string name)
    {
        string[] parts = OS.GetEnvironment(name).Split(',');
        if (parts.Length != 3)
            return Vector3.Zero;
        return float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float x)
            && float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float y)
            && float.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float z)
            // TryParse accepts "NaN"/"Infinity", and either would put the rig on an invalid transform.
            && float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z)
            ? new Vector3(x, y, z)
            : Vector3.Zero;
    }

    // The rig the player is actually looking at, which is the only one worth animating: a hidden rig
    // stops advancing its clock (CharacterSkeleton pauses _Process off screen), so arming one leaves a
    // stale gesture to thaw and replay the next time it is shown. Derived from the same condition
    // ApplyPerspective shows them by, so the two cannot drift apart.
    private CharacterSkeleton? DrawnRig => _thirdPerson || _viewmodel == null ? _rig : _viewmodel;

    private void ApplyPerspective()
    {
        // With a separate arms rig, first person hides the body and shows the arms. WITHOUT one, hiding
        // the body would leave first person empty — which is what it was doing. Unturned's own first
        // person is not an arms-only viewmodel anyway: the character is drawn from inside its own head,
        // which is why you can look down and see your legs. So the body stays visible and the camera,
        // already at eye height inside the head, sees it from within: the head's own faces point away
        // and are culled, and what is left in view is the torso, arms and legs — the swing included.
        _model.Visible = _thirdPerson || _viewmodel == null;
        if (_viewmodel != null)
            _viewmodel.Visible = !_thirdPerson;
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
