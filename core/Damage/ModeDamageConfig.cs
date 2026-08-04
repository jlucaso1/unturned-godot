namespace UnturnedGodot.Damage;

// The slice of Provider.modeConfigData every damage calculation reads, with the values PlayConfigData
// writes for EGameMode.NORMAL — the mode this port runs.
//
// A record rather than constants because these are the numbers a server operator edits: Unturned writes
// them to the server's own Config.json and reloads them from there, and EASY/HARD differ from NORMAL in
// several of them. Nothing loads that file yet, so Normal below is what everything uses; when it is
// loaded, the loader fills one of these and the maths above it does not change.
public sealed record ModeDamageConfig
{
    // Zombies.Damage_Multiplier: what a zombie's own swing does to a player. Not read by the punch, and
    // carried here because the block it belongs to is the one being ported.
    public float ZombieDamage { get; init; } = 1f;

    // Zombies.Armor_Multiplier / NonHeadshot_Armor_Multiplier: the global armor scaling applied to a
    // headshot and to everything else respectively. NORMAL leaves both at 1; EASY raises the first to
    // 1.25 and HARD lowers it to 0.75, which is the difficulty dial for how tough zombies feel.
    public float ZombieArmor { get; init; } = 1f;
    public float ZombieNonHeadshotArmor { get; init; } = 1f;

    // Zombies.Backstab_Multiplier: a hit that lands on a zombie's BACK (the swing travelling the same
    // way the zombie faces) is worth this much more. Mode-independent in PlayConfigData.
    public float ZombieBackstab { get; init; } = 1.25f;

    // Barricades/Structures/Vehicles.Melee_Damage_Multiplier: how much of a melee hit a buildable takes.
    public float BarricadeMelee { get; init; } = 1f;
    public float StructureMelee { get; init; } = 1f;
    public float VehicleMelee { get; init; } = 1f;

    // Objects.Resource_Drops_Multiplier: how many items a felled resource yields. Nothing drops loot yet;
    // the field is here so the drop maths has its multiplier when it arrives.
    public float ResourceDrops { get; init; } = 1f;

    public static readonly ModeDamageConfig Normal = new();
}
