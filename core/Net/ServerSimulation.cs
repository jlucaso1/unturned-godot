using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Player;

namespace UnturnedGodot.Net;

// One player's authoritative state on the server.
public struct PlayerMoveState
{
    public Vector3 Position;
    public Vector3 Velocity;
    public bool Grounded;
    public float Yaw;   // view yaw in degrees; movement is relative to it
    public float Pitch;
    public UnturnedGodot.Player.EPlayerStance Stance;
    public bool Moving; // input-derived (keys held), NOT position-derived: drives remote walk/idle animation
}

// How the server resolves one input frame into movement. The pure heightfield solver below covers open
// terrain and drives the deterministic tests; the Godot runtime can swap in a solver backed by real
// physics (object collision) without touching the simulation loop — the same seam Unturned crosses when
// PlayerInput re-simulates client inputs against Unity's CharacterController on the server.
public interface IMoveSolver
{
    PlayerMoveState Step(in PlayerMoveState state, in InputCommand input, float dt);
}

// Terrain height at a Godot-space (x, z); false outside the map.
public delegate bool GroundSampler(float x, float z, out float y);

// Moves with the exact core movement maths (PlayerMovement.GroundVelocity/AirVelocity, PlayerConfig's
// gravity and jump) and clamps to the terrain heightfield: the server counterpart of our
// PlayerController's grounded/air branch, minus object collision.
public sealed class HeightfieldMoveSolver : IMoveSolver
{
    private readonly GroundSampler _ground;

    public HeightfieldMoveSolver(GroundSampler ground) => _ground = ground;

    public PlayerMoveState Step(in PlayerMoveState state, in InputCommand input, float dt)
    {
        PlayerMoveState next = state;
        next.Yaw = NetAngles.DequantizeYaw(input.Yaw);
        next.Pitch = NetAngles.DequantizePitch(input.Pitch);
        next.Stance = input.Stance;
        next.Moving = input.InputX != 0 || input.InputY != 0;

        bool moving = next.Moving;
        Vector3 wishDir = Vector3.Zero;
        if (moving)
        {
            // Same frame as PlayerController: input (x, y) rotated by the body yaw; -Z is forward.
            var local = new Vector3(input.InputX, 0f, input.InputY);
            wishDir = new Basis(new Quaternion(Vector3.Up, Mathf.DegToRad(next.Yaw))) * local;
            wishDir = wishDir.Normalized();
        }

        float speed = input.Sprint && moving ? PlayerConfig.SpeedSprint : PlayerConfig.SpeedStand;

        if (next.Grounded)
        {
            Vector3 groundVel = Player.PlayerMovement.GroundVelocity(wishDir, speed);
            next.Velocity.X = groundVel.X;
            next.Velocity.Z = groundVel.Z;
            next.Velocity.Y = input.Jump ? PlayerConfig.JumpSpeed : 0f;
        }
        else
        {
            next.Velocity = Player.PlayerMovement.AirVelocity(next.Velocity, wishDir, speed, dt);
        }

        next.Position += next.Velocity * dt;

        // Terrain clamp: landing (or walking) snaps to the surface; stepping off a ledge goes airborne.
        if (_ground(next.Position.X, next.Position.Z, out float groundY))
        {
            if (next.Position.Y <= groundY)
            {
                next.Position.Y = groundY;
                next.Velocity.Y = 0f;
                next.Grounded = true;
            }
            else
            {
                next.Grounded = next.Position.Y - groundY < 0.001f;
            }
        }
        else
        {
            next.Grounded = false; // off the map: free fall
        }

        return next;
    }
}

// The authoritative 12.5 Hz loop: one queued input per player per tick re-simulates through the solver,
// exactly Unturned's PlayerInput/PlayerMovement.simulate cadence (RATE = 0.08 s). Starved players hold
// still (physics continue: gravity applies through an empty input).
public sealed class ServerSimulation
{
    public const float TickRate = 0.08f; // PlayerInput.RATE / Provider.UPDATE_TIME

    // How many input frames a player may have waiting. Purely a jitter buffer: the loop plays exactly
    // one per tick, so anything beyond this is not "more detail", it is the avatar falling behind the
    // player by that many ticks — permanently, since the backlog never drains at matched rates.
    public const int MaxQueuedInputs = 4; // 0.32 s of absorbed jitter

    private sealed class Entry
    {
        public PlayerMoveState State;
        public readonly Queue<InputCommand> Inputs = new();
        public InputCommand LastInput;

        // Trusted-position bookkeeping: the budget rate-limits CONSECUTIVE client claims, so it needs the
        // last claim we accepted and when. Before any claim exists there is nothing to rate-limit against —
        // the server-invented spawn is not a position the client ever occupied (a host who opens to LAN
        // after already moving would otherwise be rejected forever, frozen at spawn for everyone else).
        public bool HasVerifiedPosition;
        public uint LastAcceptedTick;
        public bool HasReceivedPositionFrame;
        public uint LastReceivedPositionFrame;
    }

    private readonly IMoveSolver _solver;
    private readonly Dictionary<byte, Entry> _players = new();

    public uint Tick { get; private set; }

    public ServerSimulation(IMoveSolver solver) => _solver = solver;

    public void AddPlayer(byte id, Vector3 spawnPosition)
    {
        _players[id] = new Entry
        {
            // Stance is spelled out because default(EPlayerStance) is 0, which is not one of them —
            // the enum starts at Sprint = 2, mirroring the game's own numbering. A player who joins and
            // says nothing is standing, and until the stance carried across starved ticks that invalid
            // 0 was hidden by the filler input defaulting to Stand.
            State = new PlayerMoveState
            {
                Position = spawnPosition,
                Grounded = false,
                Stance = EPlayerStance.Stand,
            },
        };
    }

    public void RemovePlayer(byte id) => _players.Remove(id);

    // For callers that hold the joined-player invariant (NetServer); throws on a violated invariant.
    public PlayerMoveState GetState(byte id) => _players[id].State;

    public bool TryGetState(byte id, out PlayerMoveState state)
    {
        if (_players.TryGetValue(id, out Entry? entry))
        {
            state = entry.State;
            return true;
        }
        state = default;
        return false;
    }

    public void QueueInput(byte id, in InputCommand input)
    {
        if (!_players.TryGetValue(id, out Entry? entry))
            return;

        // UDP may duplicate or reorder input datagrams. A trusted-position command older than one already
        // queued/processed must never rewind the player. Signed subtraction is the standard wrap-safe
        // sequence comparison as long as the sender cannot be over 2^31 frames ahead.
        if (input.HasPosition)
        {
            if (entry.HasReceivedPositionFrame
                && unchecked((int)(input.Frame - entry.LastReceivedPositionFrame)) <= 0)
                return;
            entry.HasReceivedPositionFrame = true;
            entry.LastReceivedPositionFrame = input.Frame;
        }
        entry.Inputs.Enqueue(input);

        // The loop plays one frame per tick, so a client that sends faster than the server ticks builds
        // a backlog that never drains — and a backlog is time: the avatar everyone else sees keeps
        // replaying inputs from seconds ago, further behind after every burst, on top of a queue that
        // grows without bound. The buffer therefore has a ceiling, and what falls off it is the STALE
        // end: where a player was two seconds ago is worth nothing when a fresher frame is in hand.
        // Bursts are ordinary — the client drains its send timer once per frame, so any hitch is
        // followed by a flurry — and a hostile client can simply send as fast as it likes.
        while (entry.Inputs.Count > MaxQueuedInputs)
            entry.Inputs.Dequeue();
    }

    // Advances one 0.08 s step for every player and returns the broadcastable snapshot list.
    public List<PlayerSnapshotState> Step()
    {
        Tick++;
        var states = new List<PlayerSnapshotState>(_players.Count);
        foreach ((byte id, Entry entry) in _players)
        {
            // Consume one input per tick; a starved player repeats "stand still" at their last view angles
            // (gravity and momentum still integrate, so a disconnect mid-air falls to the ground).
            // The STANCE carries over too: silence means we did not hear from this player, not that they
            // stood up. Defaulting it snapped a prone or crouched player upright on every late or lost
            // frame — a flicker at 12.5 Hz that the whole session sees, hitbox included.
            InputCommand input = entry.Inputs.TryDequeue(out InputCommand queued)
                ? queued
                : new InputCommand(0, 0, 0, false, false, entry.LastInput.Yaw, entry.LastInput.Pitch,
                    entry.State.Stance, entry.State.Grounded);
            entry.LastInput = input;

            if (input.HasPosition)
                ApplyTrustedPosition(entry, input);
            else
                entry.State = _solver.Step(entry.State, input, TickRate);

            states.Add(new PlayerSnapshotState(id, entry.State.Position,
                NetAngles.QuantizePitch(entry.State.Pitch), NetAngles.QuantizeYaw(entry.State.Yaw),
                entry.State.Stance, entry.State.Moving, entry.State.Grounded));
        }
        return states;
    }

    // Unturned's forceTrustClient shape: the client resolved collision against the full world (objects,
    // buildings) that the heightfield solver can't. The first claim is the baseline; each later claim must
    // fit a speed budget scaled by the time since the last ACCEPTED one, so packet loss widens the window
    // instead of poisoning every subsequent frame, while a genuine teleport still rubber-bands.
    private void ApplyTrustedPosition(Entry entry, in InputCommand input)
    {
        entry.State.Yaw = NetAngles.DequantizeYaw(input.Yaw);
        entry.State.Pitch = NetAngles.DequantizePitch(input.Pitch);
        entry.State.Stance = input.Stance;
        entry.State.Moving = input.InputX != 0 || input.InputY != 0;
        entry.State.Grounded = input.Grounded; // the owner's real IsOnFloor (trusted like the position)

        if (!entry.HasVerifiedPosition)
        {
            entry.State.Position = input.Position;
            entry.HasVerifiedPosition = true;
            entry.LastAcceptedTick = Tick;
            return;
        }

        uint elapsedTicks = Math.Max(1, Tick - entry.LastAcceptedTick);
        float elapsed = elapsedTicks * TickRate;
        Vector3 delta = input.Position - entry.State.Position;
        float horizontal = new Vector2(delta.X, delta.Z).Length();
        // Horizontal motion is bounded by sprint, while vertical motion independently allows terminal
        // fall speed. Combining them into one 107 m/s scalar budget let a client move ~12.8 m sideways in
        // one 80 ms tick. The 1.5x allowance absorbs tick jitter/step-up without granting that loophole.
        float horizontalBudget = PlayerConfig.SpeedSprint * elapsed * 1.5f;
        float verticalBudget = -PlayerConfig.TerminalVelocity * elapsed * 1.5f;
        if (horizontal <= horizontalBudget && MathF.Abs(delta.Y) <= verticalBudget)
        {
            entry.State.Position = input.Position;
            entry.LastAcceptedTick = Tick;
        }
    }
}
