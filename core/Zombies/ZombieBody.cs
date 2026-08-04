namespace UnturnedGodot.Zombies;

// The shape the host sweeps a zombie with, and the probes it grounds and sees with.
//
// These used to be literals inside the Godot host's move resolver. They live here because a bug-repro
// dump is replayed by pure code that has to model the SAME body: a replay that sweeps a different
// capsule than the session did is not a reproduction of anything. One definition, two users.
public static class ZombieBody
{
    // The capsule covers the body from the knees up. The full 2 m capsule dragged its base along the
    // terrain and jammed on every micro-slope; ground contact is the ground snap's job, exactly like a
    // CharacterController's grounding pass.
    public const float CapsuleBottom = 0.4f;
    public const float CapsuleTop = 2f;
    public const float CapsuleHeight = CapsuleTop - CapsuleBottom;
    public const float CapsuleCenter = (CapsuleTop + CapsuleBottom) / 2f;

    // Unity's CharacterController.Move is iterative: advance to first contact, project the rest of the
    // motion onto that surface, go again. Four passes covers a corner (two walls) plus slack.
    public const int MaxSlides = 4;

    // Step-up retries are meaningful only for one authoritative movement tick. The fastest zombie
    // covers 0.52 m at the server's 0.08 s rate; a little numeric slack keeps that valid while preventing
    // callers that validate a multi-metre route leg from treating the raised cast as a teleport over a
    // counter, fence or wall.
    public const float MaxStepMotion = 0.6f;

    // The ground probe starts just above the CharacterController's step offset (steps resolve, railings
    // above them do not) and reaches three metres down.
    public const float GroundProbeUp = 0.55f;
    public const float GroundProbeDown = 3f;

    // AlertTool's line-of-sight ray runs from the zombie's eyes and stops short of the player's own
    // collider (ZombieSystem passes eye height; the host shortens the ray).
    public const float EyeHeight = 1f;
    public const float VisionRayFraction = 0.95f;
}
