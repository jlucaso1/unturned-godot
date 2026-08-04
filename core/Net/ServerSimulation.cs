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

        // A climber is on a ladder, and PlayerMovement.simulate's CLIMB branch is what the server
        // re-simulates for them too: vertical only, no gravity, and checkGround forces them grounded.
        // Without this branch a tick with no input of its own — a lost or late datagram, which is
        // ordinary — hands the climber to the walking branch below, whose terrain clamp finds them
        // metres above the ground and calls them airborne. The next frame that arrives says grounded
        // again, and every client watching derives a landing from that edge: a thud on the ladder, and
        // the climbing footsteps cut short, on every dropped packet. Momentum is not integrated either,
        // so a burst of loss cannot start the avatar falling down the shaft it is holding onto.
        if (next.Stance == EPlayerStance.Climb)
        {
            // Unturned's own input convention is the mirror of ours: -1 is forward on the wire.
            next.Velocity = PlayerLadder.ClimbVelocity(-input.InputY, PlayerConfig.SpeedClimb);
            next.Position += next.Velocity * dt;
            next.Grounded = true;
            return next;
        }

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
        // "When" is the wall clock rather than the tick counter: those two part company the moment the
        // host stalls, since the tick loop drops the time it could not make up (NetServer.MaxCatchUpTicks)
        // while the player kept moving through all of it.
        public bool HasVerifiedPosition;
        public double LastAcceptedAt;
        // The newest frame number this player has ever sent, for every kind of input: what makes a
        // reordered datagram recognisable as stale rather than as the latest word.
        public bool HasReceivedFrame;
        public uint LastReceivedFrame;

        // Set by Teleport: the movement flag it restored is held until the client sends an input of
        // its own, because the filler input the loop invents in the meantime holds no keys.
        public bool HoldMovementUntilInput;
    }

    private readonly IMoveSolver _solver;
    private readonly Dictionary<byte, Entry> _players = new();
    private double _clock;

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
            // Stance is spelled out because default(EPlayerStance) is 0, which is Climb — the enum
            // mirrors the game's own numbering. A player who joins and says nothing is standing, not
            // hanging off a ladder that does not exist, and until the stance carried across starved
            // ticks that default was hidden by the filler input defaulting to Stand.
            State = new PlayerMoveState
            {
                Position = spawnPosition,
                Grounded = false,
                Stance = EPlayerStance.Stand,
            },
        };
    }

    public void RemovePlayer(byte id) => _players.Remove(id);

    // Puts a player somewhere outright, and forgets the trusted-position baseline so the client's own
    // next claim is adopted instead of being measured against where it used to be. Used by the bug-repro
    // harness to stand the reporter back where the dump was taken: without clearing the baseline the
    // speed budget would treat the jump as a cheat and rubber-band them back within a tick.
    public bool Teleport(byte id, Vector3 position) =>
        _players.TryGetValue(id, out Entry? entry)
        && Teleport(id, position, entry.State.Stance, entry.State.Moving);

    // With the stance and whether they were moving, because those are not decoration: the stealth
    // radius a player is detected at is computed from both (ZombieDetection.RadiusFor), so putting a
    // sprinting player back as a stander alerts a different set of zombies on the very first tick.
    public bool Teleport(byte id, Vector3 position, EPlayerStance stance, bool moving)
    {
        if (!_players.TryGetValue(id, out Entry? entry) || !IsFinite(position))
            return false;
        entry.State.Stance = stance;
        entry.State.Moving = moving;
        entry.HoldMovementUntilInput = moving;
        entry.State.Position = position;
        entry.State.Velocity = Vector3.Zero;
        entry.HasVerifiedPosition = false;
        // Anything already queued was produced before the client was moved and still carries the old
        // position. With the baseline cleared, the very next Step would adopt one of those outright
        // and put the player straight back where they were — the teleport undone by a datagram that
        // predates it. Only a claim made after the move may establish the new baseline.
        entry.Inputs.Clear();
        return true;
    }

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

        // A non-finite position is refused here rather than filtered later, because once one lands the
        // slot never recovers. The first trusted position is adopted outright — there is no previous one
        // to measure against — and every comparison against NaN afterwards is false, so the speed budget
        // rejects every subsequent position forever. The player stays at NaN for the rest of the session,
        // and that NaN goes out in every StateUpdate to every client.
        //
        // Ahead of the freshness guard so a refused command leaves no trace in the sequence state it was
        // refused before reaching. Behind it, the NaN would still have advanced LastReceivedFrame on its
        // way out, and a command the server threw away would be deciding which later ones it accepts.
        //
        // Worth saying what this is NOT, since the ordering looks more dramatic than it is: it does not
        // stop a client muting itself. Anything the burnt frame number would then suppress is
        // lower-numbered, and dropping those is the freshness guard doing its ordinary job — NaN or not.
        // QueueInput is per player, so the only inputs at stake are the sender's own. Moving this after
        // the guard leaves all 47 position and trusted-position tests green, which is the honest measure
        // of how much rides on it: the ordering is hygiene, not a fix for a second defect.
        if (input.HasPosition && !IsFinite(input.Position))
        {
            RejectedPositions++;
            return;
        }

        // UDP may duplicate or reorder input datagrams, so freshness is what the FRAME number says and
        // never what the socket happened to hand over last. A command older than one already queued or
        // played must not rewind the player — not just its position, but the stance, the jump and the
        // direction it was steering. Signed subtraction is the standard wrap-safe sequence comparison,
        // as long as the sender cannot be over 2^31 frames ahead.
        if (entry.HasReceivedFrame && unchecked((int)(input.Frame - entry.LastReceivedFrame)) <= 0)
            return;
        entry.HasReceivedFrame = true;
        entry.LastReceivedFrame = input.Frame;
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

    // Advances one 0.08 s step on the nominal clock: each call is one tick of simulated time. What the
    // owner of a real session calls is Step(now), because only the wall clock knows how long a stalled
    // frame actually took.
    public List<PlayerSnapshotState> Step() => Step(_clock + TickRate);

    // Advances one 0.08 s step for every player and returns the broadcastable snapshot list.
    public List<PlayerSnapshotState> Step(double now)
    {
        _clock = now;
        Tick++;
        var states = new List<PlayerSnapshotState>(_players.Count);
        foreach ((byte id, Entry entry) in _players)
        {
            // Consume one input per tick; a starved player repeats "stand still" at their last view angles
            // (gravity and momentum still integrate, so a disconnect mid-air falls to the ground).
            // The STANCE carries over too: silence means we did not hear from this player, not that they
            // stood up. Defaulting it snapped a prone or crouched player upright on every late or lost
            // frame — a flicker at 12.5 Hz that the whole session sees, hitbox included.
            bool starved = !entry.Inputs.TryDequeue(out InputCommand queued);
            InputCommand input = starved
                ? new InputCommand(0, 0, 0, false, false, entry.LastInput.Yaw, entry.LastInput.Pitch,
                    entry.State.Stance, entry.State.Grounded)
                : queued;
            entry.LastInput = input;

            // A restored player has no input of their own yet, and the filler above holds no keys — so
            // the solver would clear Moving before anyone reads it. The stealth radius a player is
            // noticed at is computed from that flag, so the tick right after a repro dump is loaded is
            // exactly the one it has to survive. Held only while STARVED: a client's own input saying
            // "no keys" is the client speaking, and it wins.
            bool holdRestoredMovement = entry.HoldMovementUntilInput && starved;
            entry.HoldMovementUntilInput = holdRestoredMovement;
            bool restoredMoving = entry.State.Moving;

            if (input.HasPosition)
                ApplyTrustedPosition(entry, input);
            else
                entry.State = _solver.Step(entry.State, input, TickRate);
            if (holdRestoredMovement)
                entry.State.Moving = restoredMoving;

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
            entry.LastAcceptedAt = _clock;
            return;
        }

        // Never less than one tick (a claim in the same instant still gets a tick's worth of slack) and
        // otherwise exactly the time that passed — including the seconds a stalled host could not
        // simulate. Counting ticks instead would judge a five-second stall as a fraction of a second,
        // freeze the avatar outside a window narrower than the player's real speed, and hold it there
        // for as long again while the window inched open.
        var elapsed = (float)Math.Max(TickRate, _clock - entry.LastAcceptedAt);
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
            entry.LastAcceptedAt = _clock;
        }
    }
}
