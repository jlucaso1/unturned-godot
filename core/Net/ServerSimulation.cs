using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Player;

namespace UnturnedGodot.Net;

// One hand animation a player performed on a tick, for the server to replicate.
//
// `Yaw`/`Pitch`/`Stance` are the ones the input frame that ANNOUNCED the gesture carried, not the ones
// the tick it goes out on happens to be simulating. The two part company whenever a burst of datagrams
// arrives together: a swing is accepted the moment its packet lands (see AcceptSwing), while the loop
// still plays one queued movement frame per tick, so by the time the gesture is emitted the state can be
// several frames behind the frame the player actually swung on. A player turns 180 degrees inside three
// ticks, so anything deciding WHAT a swing hit has to read the swing's own angles or it is aiming a
// third of a second into the past.
//
// Angles are the client's word either way — ApplyTrustedPosition takes them verbatim from whatever frame
// it plays — so carrying them here trusts the client with nothing new. The POSITION deliberately does
// not travel: that one is validated against a speed budget, and a punch resolves from the authoritative
// position rather than from a claim that has not been through it.
public readonly record struct PlayerGestureEvent(byte PlayerId, EPlayerGesture Gesture,
    byte Yaw = 0, byte Pitch = 0, EPlayerStance Stance = EPlayerStance.Stand);

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

// One player told where the server actually has them, after refusing where they said they were.
public readonly record struct PlayerPositionCorrection(byte PlayerId, Vector3 Position);

// A swing waiting for the tick that announces it, with the aim of the frame that threw it. See
// PlayerGestureEvent for why the angles travel with it rather than being read off the tick.
internal readonly record struct PendingGesture(EPlayerGesture Gesture, byte Yaw, byte Pitch,
    EPlayerStance Stance);

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

    // The least wall-clock time the server will let pass between one player's swings. The same rule as
    // PlayerEquipment's cooldown, measured the only way a server can measure anything it means to
    // enforce: on its own clock. Not in ticks, because a stall drops the ones it could not make up
    // (NetServer.MaxCatchUpTicks) while the player kept swinging through the real seconds; and not in the
    // client's frame numbers, because the client writes those.
    public static readonly double PunchCooldownSeconds =
        (Player.PlayerEquipment.PunchCooldownTicks + 1) * TickRate;

    // The most swings a player can have saved up. Two, so a stall that buffers a legitimate pair of them
    // into one drained batch pays out both — and no more than that, so idling never buys a flurry.
    public const double MaxSwingAllowance = 2;

    private sealed class Entry
    {
        public PlayerMoveState State;
        public readonly Queue<InputCommand> Inputs = new();
        public InputCommand LastInput;

        // The swing this player is owed on their next tick, and the identity of the last one accepted.
        //
        // The server does not re-decide a swing. It cannot usefully: the stance the punch gate reads is
        // the client's own word (ApplyTrustedPosition takes it verbatim), so re-running that gate here
        // buys nothing against a modified client and costs correctness against an honest one — a repeat
        // that outlives the frame it was thrown on would be judged in a stance the swing never happened
        // in. What the server owes the session is the part a client cannot be trusted with: that a player
        // cannot swing faster than the rule allows, and that one swing is shown once. The identity
        // answers the second, the clock answers the first.
        // A QUEUE, not a slot. The allowance deliberately lets a stall's worth of buffered swings through
        // together, and NetServer drains the whole transport batch before it steps — so both reach here
        // before either is broadcast, and a single slot would have the second quietly overwrite the
        // first, making the allowance buy nothing it claims to. One is emitted per tick, which is also
        // what keeps two from going out under the same tick number, where the client's freshness guard
        // would read the second as a retransmission of the first and drop it. Bounded by the allowance,
        // which is the most that can ever be accepted at once.
        public readonly Queue<PendingGesture> PendingGestures = new();
        public bool HasSwung;
        public byte LastSwingSequence;

        // How many swings this player may spend, and when that was last topped up. Starts at one so a
        // session's opening punch lands, and accrues from admission (AddPlayer seeds AllowanceAt) rather
        // than from the first swing — see AcceptSwing.
        public double SwingAllowance = 1;
        public double AllowanceAt;

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

    // The gestures this tick produced, in player order — refilled by every Step, so the caller
    // broadcasts them before stepping again. A list rather than a callback so the pure simulation stays
    // free of the transport, exactly as the snapshot list already is.
    public IReadOnlyList<PlayerGestureEvent> Gestures => _gestures;
    private readonly List<PlayerGestureEvent> _gestures = new();

    // The players whose trusted position this tick REFUSED, with the position the server is holding
    // them at instead. Refilled by every Step, exactly like Gestures, and for the same reason: the
    // simulation stays free of the transport and its owner does the addressing.
    //
    // This is what makes "authoritative server" true of movement. Before it, a refused claim produced
    // nothing at all — the server quietly kept the old position, the client never read its own
    // authoritative state, and the two stayed apart for the rest of the session.
    public IReadOnlyList<PlayerPositionCorrection> Corrections => _corrections;
    private readonly List<PlayerPositionCorrection> _corrections = new();

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
            // Swing allowance accrues from the moment they are admitted; see AcceptSwing.
            AllowanceAt = _clock,
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

    // Inputs off a real socket carry the moment they arrived. That is the clock the swing rate limit is
    // measured on, and it is NOT the simulation's: QueueInput runs between steps, so _clock still
    // describes the previous tick — and after a stall it can describe one a long way back, which would
    // refuse a swing that really had waited out its cooldown.
    public void QueueInput(byte id, in InputCommand input, double receivedAt)
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
        // A swing is answered HERE, as it arrives, and never from the queue below. What the rate limit
        // measures is real elapsed time, and the queue is precisely where real time stops being legible:
        // an input can wait several ticks in it, be trimmed off it unplayed, or arrive in a burst beside
        // the frames around it. Answering on arrival also means the trim can no longer swallow a punch,
        // so nothing has to be carried past it.
        //
        // Ahead of the freshness guard, and that ordering is load-bearing. The guard exists to stop stale
        // MOVEMENT from rewinding the player, and a swing is not movement: it carries its own aim, spends
        // its own allowance, and is deduplicated by its own sequence. Behind the guard, one reordered
        // datagram would lose the punch outright — deliver a later non-swing frame before the repeats of a
        // swing and every copy of that swing is discarded as stale, so an honest hit the owner watched
        // connect deals nothing at all.
        if (input.HasSwing)
            AcceptSwing(entry, input, receivedAt);

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

    // For callers with no clock of their own — tests, and anything driving the simulation directly —
    // the last tick is the best date available and the one the loop would have used anyway.
    public void QueueInput(byte id, in InputCommand input) => QueueInput(id, input, _clock);

    // One swing, answered once. Repeats of it carry the same number and are recognised as the swing
    // already answered, so losing the first datagram costs nothing and receiving all of them costs
    // nothing either. A swing the rate limit refuses is not "answered" — see below.
    private void AcceptSwing(Entry entry, in InputCommand input, double receivedAt)
    {
        // Wrap-safe seven-bit order. Distance 0 is the repeat of the swing already answered; anything in
        // the lower half is newer and is a swing of its own, gaps and all (a client whose swings were lost
        // outright leaves them). The upper half is a swing that arrived BEHIND a newer one — plain
        // equality would read that as new and answer the same punch twice, which reordering alone is
        // enough to produce now that swings are judged ahead of the freshness guard.
        if (entry.HasSwung && ((input.SwingSequence - entry.LastSwingSequence) & 0x7F) is 0 or >= 0x40)
            return;

        // Allowance accrues with real time and a swing spends one, rather than the rule being a gap
        // since the last swing. A gap cannot be measured when a stall makes the transport hand over a
        // batch of datagrams that really arrived seconds apart: they all carry the single timestamp of
        // the update that drained them, so the second swing in a batch looks like it arrived no time
        // after the first, and refusing it would lose a punch the player legitimately threw. What did
        // pass is the time before the batch, and that is what this counts. The ceiling is what keeps an
        // idle player from banking a long burst, and it is what a client sending a hundred swings at
        // once runs into.
        // The clock this counts from is set when the player is admitted, not by their first swing. Left to
        // the first swing there is no earlier moment on record, so a stall that drains a player's opening
        // two punches together gives the second one zero elapsed time and refuses it — the exact case the
        // allowance exists for, unavailable until some prior swing had established a timestamp. Waiting
        // before swinging now banks credit the way it does for the rest of the session.
        entry.SwingAllowance = Math.Min(MaxSwingAllowance,
            entry.SwingAllowance + ((receivedAt - entry.AllowanceAt) / PunchCooldownSeconds));
        entry.AllowanceAt = receivedAt;

        // Refused, and deliberately NOT recorded as answered. The number is what the repeat frames carry
        // to survive an unreliable channel, so burning it here would spend the swing's only redundancy on
        // a refusal: a punch that arrived a hair inside the cooldown — jitter between the client's own
        // gate and this clock is enough — would have its repeats swallowed by the guard above and never
        // be answered at all, while its owner watched the swing connect. Leaving the number unspent makes
        // a refusal retryable, and the retry costs nothing: allowance is a time integral, so re-running
        // the accrual above is the same arithmetic, and a grant still spends one whatever the client does.
        if (entry.SwingAllowance < 1)
            return;

        entry.SwingAllowance -= 1;
        entry.HasSwung = true;
        entry.LastSwingSequence = input.SwingSequence;
        // The swing's own aim, not the delivering frame's: a repeat that arrives first belongs to a later
        // frame, and the punch is the one the owner threw. See InputCommand.SwingYaw.
        entry.PendingGestures.Enqueue(new PendingGesture(Player.PlayerGestures.For(input.SwingFist),
            input.SwingYaw, input.SwingPitch, input.SwingStance));
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
        _gestures.Clear();
        _corrections.Clear();
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
            {
                if (!ApplyTrustedPosition(entry, input))
                    _corrections.Add(new PlayerPositionCorrection(id, entry.State.Position));
            }
            else
            {
                entry.State = _solver.Step(entry.State, input, TickRate);
            }
            if (holdRestoredMovement)
                entry.State.Moving = restoredMoving;

            // Whatever swing arrived since the last tick goes out on this one. At most one per player per
            // tick, because an avatar cannot throw two punches inside 0.08 s and a client holds a single
            // pending swing on that basis — two events under the same tick number would read as a
            // retransmission of each other anyway.
            if (entry.PendingGestures.TryDequeue(out PendingGesture announced))
                _gestures.Add(new PlayerGestureEvent(id, announced.Gesture, announced.Yaw,
                    announced.Pitch, announced.Stance));

            states.Add(new PlayerSnapshotState(id, entry.State.Position,
                NetAngles.QuantizePitch(entry.State.Pitch), NetAngles.QuantizeYaw(entry.State.Yaw),
                entry.State.Stance, entry.State.Moving, entry.State.Grounded));
        }
        return states;
    }

    // How much slack the budgets carry over the exact speed, for tick jitter and the step-up a client's
    // own collision resolve performs inside one frame.
    private const float BudgetSlack = 1.5f;

    // Displacement below this is standing still: floats, not quantized angles, so the only noise here is
    // the client's own solver settling. One centimetre, the same threshold ZombieClientMotion uses to
    // decide whether a body is moving at all.
    private const float MovingEpsilon = 0.01f;

    // Unturned's forceTrustClient shape: the client resolved collision against the full world (objects,
    // buildings) that the heightfield solver can't. The first claim is the baseline; each later claim must
    // fit a speed budget scaled by the time since the last claim we JUDGED, so packet loss widens the
    // window instead of poisoning every subsequent frame.
    //
    // Returns false when the claim was refused, so the caller can tell the client where it really is.
    // Refusing used to be the whole of the server's answer — it kept the old position and sent nothing —
    // and the client never read its own authoritative state, so the two simply diverged in silence. It is
    // the SERVER's position that decides punch damage, zombie aggro and stealth radius, so that silence
    // read to a player as "my punches miss things next to me", with nothing on screen and no counter.
    private bool ApplyTrustedPosition(Entry entry, in InputCommand input)
    {
        entry.State.Yaw = NetAngles.DequantizeYaw(input.Yaw);
        entry.State.Pitch = NetAngles.DequantizePitch(input.Pitch);
        entry.State.Grounded = input.Grounded; // the owner's real IsOnFloor (trusted like the position)

        if (!entry.HasVerifiedPosition)
        {
            entry.State.Position = input.Position;
            entry.State.Stance = input.Stance;
            entry.State.Moving = input.InputX != 0 || input.InputY != 0;
            entry.HasVerifiedPosition = true;
            entry.LastAcceptedAt = _clock;
            return true;
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
        // one 80 ms tick.
        float horizontalBudget = PlayerConfig.SpeedSprint * elapsed * BudgetSlack;

        // UP and DOWN are different budgets, and conflating them was flight.
        //
        // The old test was MathF.Abs(delta.Y) <= terminal-fall-speed, which applies the budget for
        // FALLING to movement in the opposite direction: a client could climb at 1.5x terminal velocity,
        // about 150 m/s, for as long as it liked — accepted by the server and replicated to everyone.
        // Nothing in the game rises faster than a jump (7 m/s, and it decelerates immediately), so that
        // is the ceiling going up; falling keeps terminal velocity, which is what falling does.
        float verticalBudget = delta.Y >= 0
            ? PlayerConfig.JumpSpeed * elapsed * BudgetSlack
            : -PlayerConfig.TerminalVelocity * elapsed * BudgetSlack;

        if (horizontal > horizontalBudget || MathF.Abs(delta.Y) > verticalBudget)
        {
            // Refused — and the clock moves anyway. It used to stay put, so `elapsed` grew for as long
            // as a client kept claiming impossible positions, widening the budget until a wildly wrong
            // one was eventually ACCEPTED: the divergence healed by adopting the client's answer instead
            // of correcting it, and waiting was all a client had to do. Starvation still widens the
            // window, because that is a claim we never heard; a refusal is one we heard and judged.
            entry.LastAcceptedAt = _clock;
            // Movement is derived from what the server accepted, and it accepted nothing.
            entry.State.Moving = false;
            return false;
        }

        entry.State.Position = input.Position;
        entry.LastAcceptedAt = _clock;

        // Stance and movement come from the accepted DISPLACEMENT first, and from the input flags only
        // where the two agree.
        //
        // Both were taken from the flags verbatim, and both decide how far away a player is noticed
        // (ZombieDetection.RadiusFor). A client sending `Prone, InputX = InputY = 0` while its positions
        // walked at sprint speed was granted prone's 3 m stealth radius and a standing animation, for a
        // player crossing open ground at 7 m/s. What the server can check is the distance it just
        // accepted, and the distance is the thing that cannot be lied about — it is the same number the
        // budget above was measured against.
        bool displaced = horizontal > MovingEpsilon;
        // Either side may say "moving", and only that direction is worth defending: claiming to move
        // while standing still makes a player LOUDER, and a walk animation for someone pushing into a
        // wall is what the original does. Claiming stillness while moving is the exploit, and the
        // displacement overrules it.
        entry.State.Moving = displaced || input.InputX != 0 || input.InputY != 0;
        entry.State.Stance = StanceFor(input.Stance, horizontal / elapsed);
        return true;
    }

    // The claimed stance, or the quietest one the measured speed can actually support — whichever is
    // LOUDER. A player really can be prone and still; they cannot be prone at 7 m/s.
    //
    // The slack is the same one the budgets carry, so only a claim the displacement clearly contradicts
    // is overruled. A climber is left alone: climbing is vertical, so its horizontal speed is ~0 and the
    // floor never rises above what was claimed.
    private static EPlayerStance StanceFor(EPlayerStance claimed, float speed)
    {
        EPlayerStance floor = speed switch
        {
            > PlayerConfig.SpeedStand * BudgetSlack => EPlayerStance.Sprint,
            > PlayerConfig.SpeedCrouch * BudgetSlack => EPlayerStance.Stand,
            > PlayerConfig.SpeedProne * BudgetSlack => EPlayerStance.Crouch,
            _ => claimed,
        };
        return Loudness(floor) > Loudness(claimed) ? floor : claimed;
    }

    // How noticeable a stance is, in the order ZombieDetection ranks them: sprint 20 m, stand 12,
    // crouch and climb 6, prone 3. Ordering by this rather than by the enum, whose numbering is the
    // game's own and runs the other way.
    private static int Loudness(EPlayerStance stance) => stance switch
    {
        EPlayerStance.Sprint => 4,
        EPlayerStance.Stand => 3,
        EPlayerStance.Crouch or EPlayerStance.Climb => 2,
        EPlayerStance.Prone => 1,
        _ => 0,
    };
}
