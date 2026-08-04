using System.Collections.Generic;
using Godot;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Player;

// The ladder rules, against the geometry the game actually ships. Every ladder in the core bundle is a
// box authored lying along its own Z with its Y pointing out of the wall, and every one placed on PEI is
// rotated a quarter turn about X so that Z stands up — so a ladder's UP axis is the way it FACES, which
// is the one thing about this code that reads wrong until you know it.
public class PlayerLadderTests
{
    // A ladder standing at `origin` whose face points along +X. Its own axes: Y = the facing normal,
    // Z = the direction it is climbed, X = the width of its rungs.
    private static Transform3D LadderFacing(Vector3 facing, Vector3 origin = default)
    {
        Vector3 up = facing.Normalized();
        Vector3 along = Vector3.Up;                 // the rungs run up the world
        Vector3 across = up.Cross(along);
        return new Transform3D(new Basis(across, up, along), origin);
    }

    private static LadderContact Contact(Vector3 point, Vector3? normal = null, Transform3D? frame = null)
    {
        Transform3D f = frame ?? LadderFacing(Vector3.Right);
        return new LadderContact(point, normal ?? f.Basis.Y.Normalized(), f);
    }

    private static bool Never(Vector3 a, Vector3 b) => false;
    private static bool Never(Vector3 a) => false;

    // --- Which stances may start a climb ---

    [Theory]
    [InlineData(EPlayerStance.Stand, true)]
    [InlineData(EPlayerStance.Sprint, true)]
    [InlineData(EPlayerStance.Crouch, false)]
    [InlineData(EPlayerStance.Prone, false)]
    [InlineData(EPlayerStance.Climb, false)]
    public void OnlyAnUprightStanceCanStartClimbing(EPlayerStance stance, bool expected)
        => Assert.Equal(expected, PlayerLadder.CanTransitionToClimbing(stance));

    [Fact]
    public void TheProbeStartsHalfAMetreAboveTheFeet()
        => Assert.Equal(new Vector3(3f, 10.5f, -4f), PlayerLadder.ProbeOrigin(new Vector3(3f, 10f, -4f)));

    // --- Which hits count as a ladder's face ---

    [Fact]
    public void TheFrontAndBackFacesBothCount()
    {
        Transform3D frame = LadderFacing(Vector3.Right);

        Assert.Equal(1f, PlayerLadder.FaceAlignment(Contact(Vector3.Zero, Vector3.Right, frame)), 4);
        Assert.Equal(-1f, PlayerLadder.FaceAlignment(Contact(Vector3.Zero, Vector3.Left, frame)), 4);
        Assert.True(PlayerLadder.FacesLadder(Contact(Vector3.Zero, Vector3.Right, frame)));
        Assert.True(PlayerLadder.FacesLadder(Contact(Vector3.Zero, Vector3.Left, frame)));
    }

    // The rungs' own edges, and the thin sides of the volume: hitting one is not standing in front of it.
    [Fact]
    public void AnEdgeIsNotAFace()
    {
        Transform3D frame = LadderFacing(Vector3.Right);

        Assert.False(PlayerLadder.FacesLadder(Contact(Vector3.Zero, Vector3.Up, frame)));
        Assert.False(PlayerLadder.FacesLadder(Contact(Vector3.Zero, Vector3.Forward, frame)));
        // 0.9 is a threshold, not a rounding: 25 degrees off the face is still refused.
        Assert.False(PlayerLadder.FacesLadder(
            Contact(Vector3.Zero, new Vector3(0.9f, 0.436f, 0f).Normalized(), frame)));
    }

    // A placed object may be scaled; Unity's transform.up never is, so neither is ours.
    [Fact]
    public void AScaledLadderStillHasAUnitAxis()
    {
        Transform3D frame = LadderFacing(Vector3.Right).Scaled(new Vector3(3f, 3f, 3f));

        Assert.Equal(Vector3.Right, Contact(Vector3.Zero, Vector3.Right, frame).Up);
        Assert.True(PlayerLadder.FacesLadder(Contact(Vector3.Zero, Vector3.Right, frame)));
    }

    [Fact]
    public void OnlyMostlyUprightLaddersMayBeMountedByInteracting()
    {
        Assert.True(PlayerLadder.IsUpright(Contact(Vector3.Zero, frame: LadderFacing(Vector3.Right))));
        // A ladder lying flat on the ground faces straight up: nothing to climb.
        Assert.False(PlayerLadder.IsUpright(
            Contact(Vector3.Zero, Vector3.Up, new Transform3D(Basis.Identity, Vector3.Zero))));
        // Leaning back by ten degrees is already too much (sin 10 deg = 0.17 > 0.1).
        Transform3D tilted = LadderFacing(new Vector3(1f, 0.176f, 0f));
        Assert.False(PlayerLadder.IsUpright(Contact(Vector3.Zero, tilted.Basis.Y.Normalized(), tilted)));
    }

    // --- Where a mount puts the player ---

    [Fact]
    public void TheMountSitsOnTheLaddersOwnAxis()
    {
        // The ladder stands at x=10, z=-3 facing +X; the probe met it at x=9.9, three metres up, and a
        // metre off its centre line — where the player was standing has no say in either.
        Transform3D frame = LadderFacing(Vector3.Right, new Vector3(10f, 0f, -3f));
        LadderContact contact = Contact(new Vector3(9.9f, 3f, -2f), Vector3.Right, frame);

        // x: the ladder's own x, plus half a metre out along the face. y: the hit, less the probe's own
        // half metre, so the feet land level with it. z: the ladder's own z.
        Assert.Equal(new Vector3(10.5f, 2.5f, -3f), PlayerLadder.MountPoint(contact));
    }

    // --- One tick of the stance block ---

    private static ClimbDecision Resolve(EPlayerStance stance, LadderContact? contact,
        CapsuleSweepBlocked? blocked = null, CapsuleOccupied? occupied = null)
        => PlayerLadder.Resolve(stance, Vector3.Zero, contact, blocked ?? Never, occupied ?? Never);

    [Fact]
    public void WalkingIntoALadderMountsIt()
    {
        Transform3D frame = LadderFacing(Vector3.Right, new Vector3(4f, 0f, 0f));

        ClimbDecision decision = Resolve(EPlayerStance.Stand,
            Contact(new Vector3(3.9f, 1f, 0f), Vector3.Right, frame));

        Assert.Equal(EClimbTransition.Mount, decision.Transition);
        Assert.True(decision.IsClimbing);
        Assert.Equal(new Vector3(4.5f, 0.5f, 0f), decision.MountPoint);
    }

    [Fact]
    public void ASprinterMountsToo()
        => Assert.Equal(EClimbTransition.Mount, Resolve(EPlayerStance.Sprint, Contact(Vector3.Zero)).Transition);

    // Crouch and prone cannot become a climb, so the probe's answer is not even asked for.
    [Theory]
    [InlineData(EPlayerStance.Crouch)]
    [InlineData(EPlayerStance.Prone)]
    public void ACrouchingPlayerWalksIntoALadderAndNothingHappens(EPlayerStance stance)
    {
        ClimbDecision decision = Resolve(stance, Contact(Vector3.Zero));

        Assert.Equal(EClimbTransition.None, decision.Transition);
        Assert.False(decision.IsClimbing);
    }

    [Fact]
    public void AGlancingHitIsNotAMount()
        => Assert.Equal(EClimbTransition.None,
            Resolve(EPlayerStance.Stand, Contact(Vector3.Zero, Vector3.Up)).Transition);

    [Fact]
    public void SomethingInTheWayRefusesTheMount()
    {
        ClimbDecision decision = Resolve(EPlayerStance.Stand, Contact(Vector3.Zero),
            blocked: (_, _) => true);

        Assert.Equal(EClimbTransition.None, decision.Transition);
    }

    // The exploit the destination test exists for: a barricade mounted ON the ladder, which the sweep
    // cannot see because the capsule is already overlapping it when the sweep starts.
    [Fact]
    public void AnOccupiedDestinationRefusesTheMount()
    {
        ClimbDecision decision = Resolve(EPlayerStance.Stand, Contact(Vector3.Zero),
            occupied: _ => true);

        Assert.Equal(EClimbTransition.None, decision.Transition);
    }

    [Fact]
    public void TheDestinationIsTestedAtTheMountPointAndOnlyAfterTheSweepIsClear()
    {
        Transform3D frame = LadderFacing(Vector3.Right, new Vector3(4f, 0f, 0f));
        LadderContact contact = Contact(new Vector3(3.9f, 1f, 0f), Vector3.Right, frame);
        var asked = new List<string>();
        Vector3 tested = default;

        PlayerLadder.Resolve(EPlayerStance.Stand, new Vector3(3f, 0f, 0f), contact,
            (from, to) =>
            {
                asked.Add($"sweep {from} -> {to}");
                return false;
            },
            at =>
            {
                asked.Add("occupied");
                tested = at;
                return false;
            });

        Assert.Equal(new[] { "sweep (3, 0, 0) -> (4.5, 0.5, 0)", "occupied" }, asked);
        Assert.Equal(new Vector3(4.5f, 0.5f, 0f), tested);
    }

    [Fact]
    public void ABlockedSweepDoesNotEvenAskAboutTheDestination()
    {
        bool askedDestination = false;

        PlayerLadder.Resolve(EPlayerStance.Stand, Vector3.Zero, Contact(Vector3.Zero),
            (_, _) => true,
            _ =>
            {
                askedDestination = true;
                return false;
            });

        Assert.False(askedDestination);
    }

    [Fact]
    public void StayingOnTheLadderHoldsTheClimb()
    {
        ClimbDecision decision = Resolve(EPlayerStance.Climb, Contact(Vector3.Zero));

        Assert.Equal(EClimbTransition.Hold, decision.Transition);
        Assert.True(decision.IsClimbing);
    }

    // A held climb never re-runs the mount tests: the player is already there.
    [Fact]
    public void HoldingAsksNothingOfThePhysics()
    {
        PlayerLadder.Resolve(EPlayerStance.Climb, Vector3.Zero, Contact(Vector3.Zero),
            (_, _) => throw new Xunit.Sdk.XunitException("swept while already climbing"),
            _ => throw new Xunit.Sdk.XunitException("tested the destination while already climbing"));
    }

    [Fact]
    public void LosingTheLadderEndsTheClimb()
    {
        Assert.Equal(EClimbTransition.Dismount, Resolve(EPlayerStance.Climb, null).Transition);
        // Climbing past the top edge of the volume: the probe still hits it, but not on a face.
        Assert.Equal(EClimbTransition.Dismount,
            Resolve(EPlayerStance.Climb, Contact(Vector3.Zero, Vector3.Up)).Transition);
    }

    [Fact]
    public void NoLadderAndNoClimbIsNothingAtAll()
    {
        ClimbDecision decision = Resolve(EPlayerStance.Stand, null);

        Assert.Equal(EClimbTransition.None, decision.Transition);
        Assert.False(decision.IsClimbing);
        Assert.Equal(Vector3.Zero, decision.MountPoint);
    }

    // --- The interact path ---

    [Fact]
    public void TheInteractMountAimsLowerBecauseTheTeleportWillLift()
    {
        Transform3D frame = LadderFacing(Vector3.Right, new Vector3(10f, 0f, -3f));
        LadderContact contact = Contact(new Vector3(9.9f, 3f, -2f), Vector3.Right, frame);

        // 0.65 m out rather than 0.5, and 1.1 m below the hit rather than 0.5: the teleport adds half a
        // metre back, and the rest is the slack that lets the 0.75 m probe reach the ladder afterwards.
        Vector3 mount = PlayerLadder.InteractMountPoint(contact);
        Assert.Equal(new Vector3(10.65f, 1.9f, -3f), mount);
        Assert.Equal(new Vector3(10.65f, 2.4f, -3f), PlayerLadder.TeleportDestination(mount));

        // And what has to fit there is a standing capsule plus that lift.
        Assert.Equal(PlayerConfig.HeightStand + 0.6f, PlayerLadder.InteractTestHeight, 4);
        Assert.Equal(mount + new Vector3(0f, PlayerLadder.InteractTestHeight * 0.5f, 0f),
            PlayerLadder.InteractLineOfSightTarget(mount));
    }

    // The player lands facing the ladder, from whichever side they asked.
    [Theory]
    [InlineData(1f, 0f, 90f)]     // ladder faces +X, player comes from +X -> looks along -X
    [InlineData(-1f, 0f, -90f)]
    [InlineData(0f, 1f, 0f)]      // ladder faces +Z -> looks along -Z, which is Godot's zero yaw
    [InlineData(0f, -1f, 180f)]
    public void TheInteractMountFacesTheLadder(float faceX, float faceZ, float expectedYaw)
    {
        Transform3D frame = LadderFacing(new Vector3(faceX, 0f, faceZ));
        LadderContact front = Contact(Vector3.Zero, frame.Basis.Y.Normalized(), frame);
        LadderContact back = Contact(Vector3.Zero, -frame.Basis.Y.Normalized(), frame);

        Assert.Equal(expectedYaw, PlayerLadder.ClimbYawDegrees(front), 3);
        // Hitting the back face turns the player around, exactly the game's "+180 when alignment < 0".
        Assert.Equal(180f, Mathf.Abs(Mathf.AngleDifference(
            Mathf.DegToRad(PlayerLadder.ClimbYawDegrees(front)),
            Mathf.DegToRad(PlayerLadder.ClimbYawDegrees(back)))) * (180f / Mathf.Pi), 3);
    }

    private static bool CanInteract(EPlayerStance stance, LadderContact? contact,
        bool lineOfSight = true, bool clearance = true)
        => PlayerLadder.CanInteractClimb(stance, contact, (_, _) => lineOfSight, _ => clearance,
            out _, out _);

    [Fact]
    public void InteractingWithALadderInReachClimbsIt()
    {
        Transform3D frame = LadderFacing(Vector3.Right, new Vector3(4f, 0f, 0f));

        Assert.True(PlayerLadder.CanInteractClimb(EPlayerStance.Stand,
            Contact(new Vector3(3.9f, 2f, 0f), Vector3.Right, frame),
            (_, _) => true, _ => true, out Vector3 mount, out float yaw));

        Assert.Equal(new Vector3(4.65f, 0.9f, 0f), mount);
        Assert.Equal(90f, yaw, 3);
    }

    [Theory]
    [InlineData(EPlayerStance.Climb)]
    [InlineData(EPlayerStance.Crouch)]
    [InlineData(EPlayerStance.Prone)]
    public void InteractingIsRefusedFromAStanceThatCannotClimb(EPlayerStance stance)
        => Assert.False(CanInteract(stance, Contact(Vector3.Zero)));

    [Fact]
    public void InteractingIsRefusedWithoutALadder()
        => Assert.False(CanInteract(EPlayerStance.Stand, null));

    [Fact]
    public void InteractingIsRefusedOnAnEdge()
        => Assert.False(CanInteract(EPlayerStance.Stand, Contact(Vector3.Zero, Vector3.Up)));

    // The rule the touch path does NOT have: an angled ladder cannot be interacted onto at all.
    [Fact]
    public void InteractingIsRefusedOnAnAngledLadder()
    {
        Transform3D tilted = LadderFacing(new Vector3(1f, 0.5f, 0f));

        Assert.False(CanInteract(EPlayerStance.Stand,
            Contact(Vector3.Zero, tilted.Basis.Y.Normalized(), tilted)));
    }

    [Fact]
    public void InteractingIsRefusedThroughAWall()
        => Assert.False(CanInteract(EPlayerStance.Stand, Contact(Vector3.Zero), lineOfSight: false));

    [Fact]
    public void InteractingIsRefusedWhereThePlayerWouldNotFit()
        => Assert.False(CanInteract(EPlayerStance.Stand, Contact(Vector3.Zero), clearance: false));

    // Line of sight is traced from the HIT to the middle of the capsule the mount would create — not from
    // the player's eye, which is what makes it catch a capsule placed behind a thin wall.
    [Fact]
    public void LineOfSightIsTracedFromTheHitToTheCapsuleCentre()
    {
        Transform3D frame = LadderFacing(Vector3.Right, new Vector3(4f, 0f, 0f));
        LadderContact contact = Contact(new Vector3(3.9f, 2f, 0f), Vector3.Right, frame);
        Vector3 from = default, to = default;

        PlayerLadder.CanInteractClimb(EPlayerStance.Stand, contact,
            (a, b) => { from = a; to = b; return true; }, _ => true, out Vector3 mount, out _);

        Assert.Equal(contact.Point, from);
        Assert.Equal(PlayerLadder.InteractLineOfSightTarget(mount), to);
    }

    // --- The climb branch of the movement tick ---

    [Fact]
    public void ClimbingIsVerticalAtHalfTheClimbSpeed()
    {
        Assert.Equal(new Vector3(0f, 2.25f, 0f), PlayerLadder.ClimbVelocity(1f, PlayerConfig.SpeedClimb));
        Assert.Equal(new Vector3(0f, -2.25f, 0f), PlayerLadder.ClimbVelocity(-1f, PlayerConfig.SpeedClimb));
        Assert.Equal(Vector3.Zero, PlayerLadder.ClimbVelocity(0f, PlayerConfig.SpeedClimb));
    }

    [Fact]
    public void StrafingOnALadderStillCountsAsMoving()
    {
        Assert.True(PlayerLadder.IsMoving(1f, 0f));
        Assert.True(PlayerLadder.IsMoving(0f, -1f));
        Assert.False(PlayerLadder.IsMoving(0f, 0f));
        Assert.False(PlayerLadder.IsMoving(0.05f, -0.05f)); // inside the deadzone
    }

    [Fact]
    public void HoldingForwardQueuesAStepOffTheTop()
    {
        // Facing -Z (Godot's zero yaw): the queued boost points the same way.
        Assert.Equal(new Vector3(0f, 0f, -0.5f), PlayerLadder.LaunchVelocity(Basis.Identity, 1f));

        Basis turned = new Basis(Vector3.Up, Mathf.DegToRad(90f));
        Vector3 boost = PlayerLadder.LaunchVelocity(turned, 1f);
        Assert.Equal(-0.5f, boost.X, 4);
        Assert.Equal(0f, boost.Z, 4);

        // Only forward: climbing down, or holding nothing, queues nothing.
        Assert.Equal(Vector3.Zero, PlayerLadder.LaunchVelocity(Basis.Identity, -1f));
        Assert.Equal(Vector3.Zero, PlayerLadder.LaunchVelocity(Basis.Identity, 0f));
    }

    // --- What the rest of the player reads off the stance ---

    [Fact]
    public void AClimberIsAStandingCapsuleMovingAtTheClimbSpeed()
    {
        Assert.Equal(PlayerConfig.SpeedClimb, PlayerConfig.SpeedFor(EPlayerStance.Climb));
        Assert.Equal(PlayerConfig.HeightStand, PlayerConfig.HeightFor(EPlayerStance.Climb));
        Assert.Equal(PlayerConfig.EyeHeightStand, PlayerConfig.EyeHeightFor(EPlayerStance.Climb));
    }

    // MIN_ANGLE_CLIMB 45 / MAX_ANGLE_CLIMB 100 in the game's own 0=up, 90=forward, 180=down convention.
    [Fact]
    public void AClimberCannotLookStraightDownTheLadder()
    {
        (float down, float up) = PlayerConfig.PitchLimitsFor(EPlayerStance.Climb);

        Assert.Equal(-10f, down);
        Assert.Equal(45f, up);
    }

    // The whole crouch/prone/sprint block sits behind `stance != CLIMB`, and the intents are cleared while
    // it does, so a toggle pressed on a ladder is not waiting to fire when the player steps off.
    [Fact]
    public void StanceIntentsDoNotApplyToAClimber()
    {
        Assert.Equal(EPlayerStance.Climb, PlayerStanceMachine.Resolve(EPlayerStance.Climb,
            wantCrouch: true, wantProne: true, wantSprint: true, isMoving: true, hasStamina: true,
            canStand: true, canCrouch: true));
        Assert.True(PlayerStanceMachine.ClearsStanceIntents(EPlayerStance.Climb));
        Assert.False(PlayerStanceMachine.ClearsStanceIntents(EPlayerStance.Stand));
    }
}
