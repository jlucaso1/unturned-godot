namespace UnturnedGodot.Player;

// The on-foot stance resolution from Unturned's PlayerStance.simulate, as a pure function. Crouch and prone
// intents win (you can always drop lower); returning to a taller stance needs headroom (canStand / canCrouch
// from a capsule clearance check); sprint is only entered from a moving stand with stamina.
public static class PlayerStanceMachine
{
    public static bool NeedsStandClearance(EPlayerStance current, bool wantCrouch, bool wantProne)
        => !wantCrouch && !wantProne && current is EPlayerStance.Crouch or EPlayerStance.Prone;

    public static bool NeedsCrouchClearance(EPlayerStance current, bool wantCrouch)
        => wantCrouch && current == EPlayerStance.Prone;

    // While on a ladder the crouch and prone intents are cleared outright (PlayerStance.Update), so a
    // toggle pressed mid-climb does not drop the player into a crouch the moment they step off the top.
    public static bool ClearsStanceIntents(EPlayerStance stance) => stance == EPlayerStance.Climb;

    public static EPlayerStance Resolve(
        EPlayerStance current, bool wantCrouch, bool wantProne, bool wantSprint,
        bool isMoving, bool hasStamina, bool canStand, bool canCrouch)
    {
        // A climber's stance is the ladder's to decide (PlayerLadder.Resolve); the whole crouch/prone/
        // sprint block sits behind `stance != CLIMB` in the game. Leaving a ladder is a dismount, which
        // hands back a stander before this is ever asked.
        if (current == EPlayerStance.Climb)
            return EPlayerStance.Climb;

        if (wantCrouch)
        {
            // Dropping from stand always fits; raising from prone to crouch needs the crouch headroom.
            if (current == EPlayerStance.Prone && !canCrouch)
                return EPlayerStance.Prone;
            return EPlayerStance.Crouch;
        }

        if (wantProne)
            return EPlayerStance.Prone;

        // No crouch/prone intent: stand back up if we were low, but only with headroom above.
        if (current is EPlayerStance.Crouch or EPlayerStance.Prone)
            return canStand ? EPlayerStance.Stand : current;

        // Standing: sprint only while actually moving and not exhausted; otherwise plain stand.
        if (wantSprint && isMoving && hasStamina)
            return EPlayerStance.Sprint;
        return EPlayerStance.Stand;
    }
}
