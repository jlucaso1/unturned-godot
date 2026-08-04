namespace UnturnedGodot;

// The physics layer each kind of body lives on, in one place.
//
// They were spread across three files as bare numbers and `1u << n` constants, and two of them collided:
// the player was put on bit 1 while VisionBlocker was defined as `1u << 1`. Both sites carried a comment
// asserting that could not happen — PlayerController said the zombie brain's vision rays "all mask 1",
// and NetworkManager said BLOCK_VISION hits object colliders and "crucially not the players themselves,
// (an unfiltered ray ends inside the target's own capsule at close range and blinds every nearby zombie)".
// The second comment describes the bug the first one caused.
//
// Kept here rather than beside the bodies so the layout is stated once and the invariants below can be
// tested — src/ is excluded from coverage, which is how a two-file contradiction went unnoticed.
public static class CollisionLayers
{
    // Terrain and LARGE object collision: the solid world a character walks on and into.
    public const uint World = 1u << 0;

    // What Unturned's RayMasks.BLOCK_VISION hits — the LARGE and MEDIUM object colliders, and nothing
    // else. Zombie alert raycasts query exactly this bit, so anything that must not blind a zombie by
    // standing in its own line of sight has to stay off it.
    public const uint VisionBlocker = 1u << 1;

    // MEDIUM furniture (gravestones, benches, beds): collides with the player, but not by itself with
    // zombie movement. ObjectCollisionPolicy additionally gives fence assets the World bit because a
    // continuous fence is a character barrier, not furniture a zombie may shove through.
    public const uint MediumFurniture = 1u << 2;

    // Characters: the local player and the headless movement probe. Deliberately its own bit and not
    // VisionBlocker, which is what the two comments above each assumed and neither enforced.
    public const uint Player = 1u << 3;

    // What a character collides against: the solid world plus the furniture it should not walk through.
    public const uint CharacterMask = World | MediumFurniture;
}
