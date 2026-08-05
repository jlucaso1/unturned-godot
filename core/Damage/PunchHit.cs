using Godot;

namespace UnturnedGodot.Damage;

// What a punch's raycast found: SDG.Unturned.RaycastInfo, trimmed to the fields the damage branches
// read. `Id` names the target within its kind — a zombie's replicated id, or the index of a placed tree
// or object in the level's own list, which is this port's equivalent of the (region x, y, index) triple
// ResourceManager.tryGetRegion hands back.
public readonly record struct PunchHit(
    EPunchTargetKind Kind,
    int Id,
    ELimb Limb,
    Vector3 Point,
    float Distance)
{
    public static readonly PunchHit None = new(EPunchTargetKind.None, 0, ELimb.Spine, Vector3.Zero, 0f);

    public bool Exists => Kind != EPunchTargetKind.None;

    // A destructible object is damaged one SECTION at a time (InteractableObjectRubble.askDamage takes
    // one), and 255 means "all of them" in the original. Nothing here splits an object into sections
    // yet, so every object hit is the whole thing — which is what the original's own byte.MaxValue
    // means, and keeps the field honest rather than pretending to a section index it did not resolve.
    public const byte AllSections = byte.MaxValue;
}
