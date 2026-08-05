namespace UnturnedGodot.Damage;

// What a bare fist may hit. Unturned's ERaycastInfoType, trimmed to the cases a punch actually branches
// on in PlayerEquipment.punch; the ones this port has no world for yet are still listed, because the
// damage table below carries a number for every one of them and a target it cannot reach is a target
// whose number would otherwise have nowhere to live.
public enum EPunchTargetKind : byte
{
    None,
    Player,
    Zombie,
    Animal,
    Vehicle,
    Barricade,
    Structure,
    Resource,
    Object,
}

// The punch's own data, straight off SDG.Unturned.PlayerEquipment.
//
// In the original these are `private static readonly` fields at the top of PlayerEquipment: the fist is
// not an item, has no .dat behind it, and its numbers are therefore code. Ported as data anyway — one
// table, named and commented — because the whole point of a punch is that it is the WEAKEST damage
// source in the game, and every other one is measured against it: a machete is a Useable with an asset
// carrying this same shape (a per-limb multiplier plus a number for each kind of world target), so the
// resolution in PunchDamageResolver is written against the table rather than against the punch.
//
// The `> 1` gates below are the original's own, and they are why several of these do nothing: a target
// whose punch damage is not GREATER than one is skipped outright, both for the crosshair hitmark on the
// client and — for the vehicle, which is the one set to zero — for the damage itself.
public static class PunchDamageData
{
    // simulate_PunchInput: "simulation - lastPunch > 5", so a fist swings once every six simulation
    // ticks. Mirrored by PlayerEquipment.PunchCooldownTicks, which is where the input gate reads it.

    // DamageTool.raycast(ray, 1.75f, RayMasks.DAMAGE_CLIENT): how far in front of the eyes a fist
    // reaches. Metres, and short enough that the arm has to be nearly touching what it hits.
    public const float Reach = 1.75f;

    // The server's own sanity check on the hit the client reported: "(info.point - aim.position)
    // .sqrMagnitude > 36" — anything past six metres is discarded whatever the client claims. Kept as
    // the squared value the comparison actually uses.
    public const float MaxServerHitDistance = 6f;
    public const float MaxServerHitDistanceSquared = MaxServerHitDistance * MaxServerHitDistance;

    // DAMAGE_PLAYER_MULTIPLIER: 15 base, and a fist is worth less against limbs and more against a
    // skull. Fifteen to the chest, sixteen and a half to the head, nine to an arm or a leg.
    public static readonly PlayerDamageMultiplier Player = new(15f, 0.6f, 0.6f, 0.8f, 1.1f);

    // DAMAGE_ZOMBIE_MULTIPLIER: the same 15 base, but a zombie's limbs shrug a fist off harder than a
    // player's do (0.3 rather than 0.6) — punching a zombie in the arm is worth four and a half.
    public static readonly ZombieDamageMultiplier Zombie = new(15f, 0.3f, 0.3f, 0.6f, 1.1f);

    // DAMAGE_ANIMAL_MULTIPLIER: 15 base with no arm column, since an animal has none.
    public static readonly AnimalDamageMultiplier Animal = new(15f, 0.3f, 0.6f, 1.1f);

    // The flat numbers for everything that is not a body. Each is compared against the `> 1` gate before
    // it is used, so Vehicle's zero is not "a punch does nothing to a car" by arithmetic — the branch is
    // skipped entirely, and the crosshair does not even acknowledge the car. It is commented `//5;` in
    // the original: the number a fist used to do, deliberately turned off.
    public const float Barricade = 2f;
    public const float Structure = 2f;
    public const float Vehicle = 0f;

    // Twenty against a resource, which is the largest number in this table by a wide margin and still
    // leaves the smallest tree in the game (Health 800-1400 in Bundles/Trees) at forty-odd punches.
    // Resources are also the one target with an explicit opt-in: ResourceAsset.Vulnerable_To_Fists.
    public const float Resource = 20f;

    // And five against a destructible object's rubble, gated on the object's own rubbleBladeID being 0 —
    // a fist carries no blade, so a section that asks for one is immune to it.
    public const float Object = 5f;

    // PlayerEquipment's own gate, spelled out once: a target whose number is not greater than one is not
    // hit at all. Applied to the flat numbers above; the three multiplier tables gate on their `damage`
    // field the same way, which for the punch is 15 for all three.
    public static bool CanDamage(float amount) => amount > 1f;

    // The flat number for a target kind, or 0 for the kinds that carry a multiplier table instead.
    public static float FlatFor(EPunchTargetKind kind) => kind switch
    {
        EPunchTargetKind.Barricade => Barricade,
        EPunchTargetKind.Structure => Structure,
        EPunchTargetKind.Vehicle => Vehicle,
        EPunchTargetKind.Resource => Resource,
        EPunchTargetKind.Object => Object,
        _ => 0f,
    };

    // Whether a punch is allowed to reach this kind of target at all, before anything about the
    // particular target (its asset's invulnerability, a resource's Vulnerable_To_Fists) is consulted.
    public static bool CanDamage(EPunchTargetKind kind) => kind switch
    {
        EPunchTargetKind.None => false,
        EPunchTargetKind.Player => CanDamage(Player.Damage),
        EPunchTargetKind.Zombie => CanDamage(Zombie.Damage),
        EPunchTargetKind.Animal => CanDamage(Animal.Damage),
        _ => CanDamage(FlatFor(kind)),
    };
}
