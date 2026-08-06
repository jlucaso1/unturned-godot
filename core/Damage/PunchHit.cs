using Godot;

namespace UnturnedGodot.Damage;

// What a punch's raycast found: SDG.Unturned.RaycastInfo, trimmed to the fields the damage branches
// read. `Id` names the target within its kind — a zombie's replicated id, or the index of a placed tree
// or object in the level's own list, which is this port's equivalent of the (region x, y, index) triple
// ResourceManager.tryGetRegion hands back.
//
// `Normal` and `Surface` describe where the ray STOPPED rather than what it damaged, and they are filled
// in even when nothing here can be hurt. That split is the original's: PlayerEquipment.punch spawns its
// impact off `info.materialName` before it looks at info.type at all, so a fist against a brick wall
// cracks and thumps exactly like one against a breakable crate — the wall simply takes no damage.
public readonly record struct PunchHit(
    EPunchTargetKind Kind,
    int Id,
    ELimb Limb,
    Vector3 Point,
    float Distance,
    Vector3 Normal = default,
    string Surface = "")
{
    public static readonly PunchHit None = new(EPunchTargetKind.None, 0, ELimb.Spine, Vector3.Zero, 0f);

    // Something the punch can damage. Deliberately NOT what the impact effect is gated on — see Surface.
    public bool Exists => Kind != EPunchTargetKind.None;

    // The fist met a surface that says what it is made of, so it makes a sound and leaves a mark. Mirrors
    // the original's own `!string.IsNullOrEmpty(info.materialName)`, null-tolerant for the same reason it
    // is there: a collider with no PhysicMaterial produces no name and no effect.
    public bool HasSurface => !string.IsNullOrEmpty(Surface);

    // A destructible object is damaged one SECTION at a time (InteractableObjectRubble.askDamage takes
    // one), and 255 means "all of them" in the original. Nothing here splits an object into sections
    // yet, so every object hit is the whole thing — which is what the original's own byte.MaxValue
    // means, and keeps the field honest rather than pretending to a section index it did not resolve.
    public const byte AllSections = byte.MaxValue;
}
