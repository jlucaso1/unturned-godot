using System;

namespace UnturnedGodot.Player;

// Unturned's PlayerEquipment.EAttackInputFlags: what the attack buttons did this simulation tick, rather
// than whether they are held. A tick that neither starts nor stops carries None; holding a button down
// produces Start once, on the tick the press happened.
[Flags]
public enum EAttackInputFlags : byte
{
    None = 0,
    Start = 1,
    Stop = 2,
}

// Which fist swung. Unturned's primary attack throws the left, the secondary the right.
public enum EPlayerPunch : byte { Left, Right }

// SDG.Unturned.EPlayerGesture, in the game's own order — it is a [NetEnum], so the ordinals are the
// values the wire carries. The whole set is ported because a gesture is how every hand interaction
// announces itself to the other players; only the punches are acted on so far, and PlayerGestures says
// which ones have an animation behind them.
public enum EPlayerGesture : byte
{
    None,
    InventoryStart,
    InventoryStop,
    Pickup,
    PunchLeft,
    PunchRight,
    SurrenderStart,
    SurrenderStop,
    Point,
    Wave,
    Salute,
    ArrestStart,
    ArrestStop,
    RestStart,
    RestStop,
    Facepalm,
    TPoseStart,
    TPoseStop,
}

// The clip each gesture plays, by the name the character's own Animation component gives it. Gestures the
// port has no animation for yet resolve to null and are simply not played, which is also what Unturned
// does with a clip a given character does not carry.
public static class PlayerGestures
{
    public static string? ClipFor(EPlayerGesture gesture) => gesture switch
    {
        EPlayerGesture.PunchLeft => "Punch_Left",
        EPlayerGesture.PunchRight => "Punch_Right",
        _ => null,
    };

    public static EPlayerGesture For(EPlayerPunch punch) =>
        punch == EPlayerPunch.Left ? EPlayerGesture.PunchLeft : EPlayerGesture.PunchRight;
}

// What the player's hands may do this tick, as the equipment simulation needs to see it. A struct rather
// than a pile of parameters because this is the list that grows: swimming, safezones, arrest and the
// equipped item's own busy state all gate the same decision in the original.
public readonly record struct HandState
{
    public required EPlayerStance Stance { get; init; }

    // PlayerEquipment.isBusy — an equip/dequip/inspect in progress swallows attack input. Nothing sets
    // it yet; it is here so the gate exists at the point that will need it rather than being retrofitted
    // around the caller.
    public bool IsBusy { get; init; }
}

// Ports the punch half of PlayerEquipment.simulate: the per-tick decision of whether an attack input
// throws a fist, and which one. Pure and stateful only in its cooldown, so the client (predicting its own
// swing) and the server (authoritative, replicating it to everyone else) run the identical rule and cannot
// disagree about whether a punch happened.
//
// This is the seam the rest of the hand interactions grow from. An equipped item in Unturned replaces the
// punch with its Useable — a melee swing, a gun's shot, a consumable — driven by these same two input
// flags on this same tick. Simulate is therefore written to answer "what did the hands do", not "did we
// punch": when a Useable exists it takes the input instead, and the gesture it produces travels the same
// way.
public sealed class PlayerEquipment
{
    // simulate_PunchInput: "simulation - lastPunch > 5", so a fist may swing once every six simulation
    // ticks — 0.48 s at PlayerInput.RATE.
    public const uint PunchCooldownTicks = 5;

    private uint _lastPunch;
    private bool _hasPunched;

    // The tick a punch was last thrown on, for callers that resume or replicate this state.
    public uint LastPunchTick => _lastPunch;

    // Returns the gesture this tick produced, or None. `tick` is the caller's own monotonic 12.5 Hz
    // counter — the server's simulation tick on one end, the client's input frame number on the other.
    // The two need not agree on a value, only on a rate: every rule here measures an ELAPSED number of
    // ticks against this instance's own last swing, so a constant offset between the ends changes
    // nothing. A rule that ever compares one end's tick to the other's would need more than this.
    public EPlayerGesture Simulate(uint tick, EAttackInputFlags primary, EAttackInputFlags secondary,
        in HandState hands)
    {
        if (primary.HasFlag(EAttackInputFlags.Start) && TryPunch(tick, hands))
            return EPlayerGesture.PunchLeft;
        if (secondary.HasFlag(EAttackInputFlags.Start) && TryPunch(tick, hands))
            return EPlayerGesture.PunchRight;
        return EPlayerGesture.None;
    }

    // The gates the original applies before a fist may swing. Prone is the stance one: there is no prone
    // punch animation, and the game simply does not throw one.
    private bool TryPunch(uint tick, in HandState hands)
    {
        if (hands.IsBusy || hands.Stance == EPlayerStance.Prone)
            return false;
        // Wrap-safe: the counter is a uint that runs for the length of a session, and unsigned
        // subtraction gives the elapsed ticks correctly across its wrap.
        if (_hasPunched && unchecked(tick - _lastPunch) <= PunchCooldownTicks)
            return false;

        _lastPunch = tick;
        _hasPunched = true;
        return true;
    }
}
