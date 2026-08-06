using System;
using Godot;

namespace UnturnedGodot.UI;

// SDG.Unturned.EPlayerHit: which mark the crosshair flashes, and in which colour.
public enum EPlayerHit : byte
{
    None,
    Entity,
    Critical,
    Build,
    Ghost,
}

// OptionsSettings.hitmarkerStyle. Animated is the shipped default; Classic is the fixed cross the game
// used before the animation was added, kept because it is a real option a player can be running.
public enum EHitmarkerStyle : byte
{
    Animated,
    Classic,
}

// The maths behind SleekHitmarker: where each of the four wedges sits, how it is turned, and how long the
// whole thing lasts.
//
// Pure, because it is the part worth pinning. The four wedges are one texture rotated into each quadrant,
// and the animation slides them outward from the centre along their own diagonals — get a sign wrong and
// they cross over each other instead, which reads as a smear rather than a hit.
public static class Hitmarker
{
    // PlayerUI.HIT_TIME. The mark's whole life, and the duration its slide is linear over.
    public const float LifetimeSeconds = 0.33f;

    // SleekHitmarker's images: 16 px square, drawn centred on their own position.
    public const int ImageSize = 16;
    public const int HalfImageSize = ImageSize / 2;

    // PlayAnimation: the wedges start a tenth of the way out and finish half the way out, in screen
    // fractions from the centre.
    public const float StartOffset = 0.1f;
    public const float EndOffset = 0.5f;

    // The randomized tilt, in degrees. Small — it stops repeated hits stamping an identical mark.
    public const float MaxAngleJitterDegrees = 3f;

    // ApplyClassicPositions: a fixed 16 px diagonal cross, no slide.
    public const int ClassicOffset = 16;

    // OptionsSettings' defaults. Alpha 0.5 on all three, which is what makes the mark read as an overlay
    // rather than as part of the world.
    public static readonly Color EntityColor = new(1f, 1f, 1f, 0.5f);
    public static readonly Color CriticalColor = new(1f, 0f, 0f, 0.5f);
    public static readonly Color CrosshairColor = new(1f, 1f, 1f, 0.5f);

    // Which of the three icons a hit draws with. Critical shares the entity icon and differs only in
    // colour, which is exactly how SleekHitmarker.SetStyle spells it.
    public static bool UsesEntityIcon(EPlayerHit hit) => hit is EPlayerHit.Entity or EPlayerHit.Critical;

    public static Color ColorFor(EPlayerHit hit) =>
        hit == EPlayerHit.Critical ? CriticalColor : EntityColor;

    // A limb decides the colour: DAMAGE goes by the whole table, but the MARK only asks whether it was a
    // skull. PlayerEquipment.punch writes this as `info.limb == ELimb.SKULL ? CRITICAL : ENTITIY`.
    public static EPlayerHit ForLimb(UnturnedGodot.Damage.ELimb limb) =>
        limb == UnturnedGodot.Damage.ELimb.Skull ? EPlayerHit.Critical : EPlayerHit.Entity;

    // The four wedges, north-east first and then clockwise. `t` runs 0..1 across HIT_TIME.
    //
    // Each wedge is turned a quarter more than the last and slides along its own diagonal, so the set
    // stays a cross as it opens. The jitter turns all four together — it is one mark, tilted, not four
    // independently wobbling pieces.
    public static void Animated(float t, float jitterDegrees, Span<Vector2> positions,
        Span<float> rotationsDegrees)
    {
        if (positions.Length < 4 || rotationsDegrees.Length < 4)
            throw new ArgumentException("A hitmarker has four wedges.", nameof(positions));

        t = Math.Clamp(t, 0f, 1f);
        float offset = Mathf.Lerp(StartOffset, EndOffset, t);

        // The NE wedge travels at -45 degrees from the jitter; each following one is a quarter turn on.
        float direction = Mathf.DegToRad(jitterDegrees - 45f);
        for (int i = 0; i < 4; i++)
        {
            float angle = direction + (i * Mathf.Pi / 2f);
            positions[i] = new Vector2(0.5f + (MathF.Cos(angle) * offset),
                0.5f + (MathF.Sin(angle) * offset));
            rotationsDegrees[i] = (i * 90f) + jitterDegrees;
        }
    }

    // ApplyClassicPositions: the wedges sit at fixed pixel offsets from the centre and do not move.
    // Returned as pixel offsets rather than screen fractions, which is how the original places them.
    public static void Classic(Span<Vector2> pixelOffsets, Span<float> rotationsDegrees)
    {
        if (pixelOffsets.Length < 4 || rotationsDegrees.Length < 4)
            throw new ArgumentException("A hitmarker has four wedges.", nameof(pixelOffsets));

        // NE, SE, SW, NW — the same order Animated uses, in Godot's screen space where +Y is down.
        pixelOffsets[0] = new Vector2(ClassicOffset, -ClassicOffset);
        pixelOffsets[1] = new Vector2(ClassicOffset, ClassicOffset);
        pixelOffsets[2] = new Vector2(-ClassicOffset, ClassicOffset);
        pixelOffsets[3] = new Vector2(-ClassicOffset, -ClassicOffset);
        for (int i = 0; i < 4; i++)
            rotationsDegrees[i] = i * 90f;
    }
}
