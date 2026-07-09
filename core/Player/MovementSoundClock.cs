namespace UnturnedGodot.Player;

// The movement-audio state machine, shared by the local player (grounded from the physics body,
// moving from held keys) and by remote players (grounded from the replicated pose vs terrain,
// moving/stance from replication) — sounds derive from state on every client, like Unturned, so
// footsteps and landings never travel over the network.
public enum EMovementSound { None, Footstep, Landed }

public sealed class MovementSoundClock
{
    private float _sinceFootstep;
    private bool _wasGrounded = true; // spawning mid-air shouldn't thud on the first ground contact? It
                                      // should: Unturned plays the landing after the spawn fall too.

    public MovementSoundClock(bool startGrounded = false) => _wasGrounded = startGrounded;

    // One frame of movement: returns the sound this frame owes, applying PlayerMovement's rules —
    // the landing on the airborne->grounded edge (which also re-arms the footstep clock), then the
    // 2.1/speed footstep cadence while grounded and moving; prone is silent for both.
    public EMovementSound Tick(EPlayerStance stance, bool moving, bool grounded, float speed, float dt)
    {
        bool landed = grounded && !_wasGrounded;
        _wasGrounded = grounded;

        if (landed && !FootstepConfig.IsSilentStance(stance))
        {
            _sinceFootstep = 0f; // PlayLandAudioClip resets lastFootstep
            return EMovementSound.Landed;
        }

        _sinceFootstep += dt;
        if (_sinceFootstep <= FootstepConfig.Interval(speed))
            return EMovementSound.None;
        _sinceFootstep = 0f;

        return grounded && moving && !FootstepConfig.IsSilentStance(stance)
            ? EMovementSound.Footstep
            : EMovementSound.None;
    }
}
