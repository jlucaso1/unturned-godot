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

    // Trusted positions refused for not being finite. Non-zero means a client sent NaN or an infinity —
    // a physics glitch on its end, or a deliberate attempt to wedge its own slot.
    public long RejectedPositions { get; private set; }

    // Kept explicit rather than using Vector3.IsFinite, at the one place that has to reason about why a
    // non-finite component is unrecoverable here rather than merely wrong.
    private static bool IsFinite(in Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    public ServerSimulation(IMoveSolver solver) => _solver = solver;

    public void AddPlayer(byte id, Vector3 spawnPosition)
    {
        _players[id] = new Entry
        {
            State = new PlayerMoveState { Position = spawnPosition, Grounded = false },
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
            // A non-finite position is refused here rather than filtered later, because once one lands the
            // slot never recovers. The first trusted position is adopted outright — there is no previous
            // one to measure against — and every comparison against NaN afterwards is false, so the speed
            // budget rejects every subsequent position forever. The player stays at NaN for the rest of
            // the session, and that NaN goes out in every StateUpdate to every client.
            if (!IsFinite(input.Position))
            {
                RejectedPositions++;
                return;
            }

            if (entry.HasReceivedPositionFrame
                && unchecked((int)(input.Frame - entry.LastReceivedPositionFrame)) <= 0)
                return;
            entry.HasReceivedPositionFrame = true;
            entry.LastReceivedPositionFrame = input.Frame;
        }
        entry.Inputs.Enqueue(input);
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
            InputCommand input = entry.Inputs.TryDequeue(out InputCommand queued)
                ? queued
                : new InputCommand(0, 0, 0, false, false, entry.LastInput.Yaw, entry.LastInput.Pitch);
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
