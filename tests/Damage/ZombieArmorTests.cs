using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnturnedGodot.Assets;
using UnturnedGodot.Damage;
using UnturnedGodot.Dat;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests.Damage;

public class ClothingArmorDatabaseTests
{
    private static string Dat(string type, ushort id, string extra = "") =>
        $"GUID 0f1b6a5a8e2c4f0a9c1d2e3f4a5b6c7d\nType {type}\nUseable Clothing\nID {id}\n{extra}";

    private static ClothingArmor Parse(string text)
    {
        Assert.True(ClothingArmorDatabase.TryParse(DatParser.Parse(text), out ClothingArmor clothing));
        return clothing;
    }

    [Fact]
    public void ReadsTypeIdAndArmor()
    {
        ClothingArmor shirt = Parse(Dat("Shirt", 1011, "Armor 0.9\n"));

        Assert.Equal(1011, shirt.Id);
        Assert.Equal(EClothingType.Shirt, shirt.Type);
        Assert.Equal(0.9f, shirt.Armor);
    }

    // "Multiplier to incoming damage. Defaults to 1.0."
    [Fact]
    public void ArmorDefaultsToOne() => Assert.Equal(1f, Parse(Dat("Pants", 1012)).Armor);

    // "Defaults to armor value if Armor_Explosion isn't specified."
    [Fact]
    public void ExplosionArmorFallsBackToArmor() =>
        Assert.Equal(0.9f, Parse(Dat("Vest", 1013, "Armor 0.9\n")).ExplosionArmor);

    [Fact]
    public void ExplosionArmorIsReadWhenNamed() =>
        Assert.Equal(0.4f, Parse(Dat("Vest", 1013, "Armor 0.9\nArmor_Explosion 0.4\n")).ExplosionArmor);

    // "if (isPro) { _armor = 1f; _explosionArmor = 1f; }" — and isPro is the bare presence of a Pro key.
    [Fact]
    public void ProItem_ForcesArmorToOne()
    {
        ClothingArmor pro = Parse(Dat("Shirt", 1014, "Pro\nArmor 0.1\n"));
        Assert.Equal(1f, pro.Armor);
        Assert.Equal(1f, pro.ExplosionArmor);
    }

    [Theory]
    [InlineData("Hat", EClothingType.Hat)]
    [InlineData("Shirt", EClothingType.Shirt)]
    [InlineData("Pants", EClothingType.Pants)]
    [InlineData("Vest", EClothingType.Vest)]
    [InlineData("Backpack", EClothingType.Backpack)]
    [InlineData("Mask", EClothingType.Mask)]
    [InlineData("Glasses", EClothingType.Glasses)]
    public void EveryClothingTypeIsRecognised(string raw, EClothingType expected) =>
        Assert.Equal(expected, Parse(Dat(raw, 2000)).Type);

    [Theory]
    [InlineData("Gun")]
    [InlineData("Melee")]
    [InlineData("Food")]
    public void NonClothingIsRejected(string type) =>
        Assert.False(ClothingArmorDatabase.TryParse(DatParser.Parse(Dat(type, 2000)), out _));

    // A zombie table names its clothing by legacy id, so an asset without one cannot be reached.
    [Fact]
    public void AssetWithoutALegacyId_IsSkipped() =>
        Assert.False(ClothingArmorDatabase.TryParse(
            DatParser.Parse("GUID 0f1b6a5a8e2c4f0a9c1d2e3f4a5b6c7d\nType Shirt\n"), out _));

    [Fact]
    public void ScanDirectory_IndexesTheClothingItAndOnlyIt()
    {
        using var dir = new TempDir();
        dir.Write(Path.Combine("Shirts", "Tee", "Tee.dat"), Dat("Shirt", 180, "Armor 0.95\n"));
        dir.Write(Path.Combine("Guns", "Eaglefire", "Eaglefire.dat"), Dat("Gun", 4));
        dir.Write(Path.Combine("Shirts", "Notes.txt"), Dat("Shirt", 999, "Armor 0.1\n"));

        ClothingArmorDatabase database = ClothingArmorDatabase.ScanDirectory(dir.Path);

        Assert.Equal(1, database.Count);
        Assert.Equal(0.95f, database.ArmorFor(180));
        Assert.Equal(1f, database.ArmorFor(4));   // not clothing: bare
        Assert.Equal(1f, database.ArmorFor(999)); // not a .dat: never scanned
    }

    [Fact]
    public void MissingDirectory_IsEmpty() =>
        Assert.Equal(0, ClothingArmorDatabase.ScanDirectory("/no/such/place").Count);

    [Fact]
    public void ScanDirectories_FirstClaimantWins()
    {
        using var official = new TempDir();
        using var mod = new TempDir();
        official.Write("a.dat", Dat("Shirt", 180, "Armor 0.95\n"));
        mod.Write("b.dat", Dat("Shirt", 180, "Armor 0.05\n"));

        ClothingArmorDatabase merged =
            ClothingArmorDatabase.ScanDirectories(new[] { official.Path, mod.Path });

        Assert.Equal(0.95f, merged.ArmorFor(180));
    }

    [Fact]
    public void IsVest_OnlyForVests()
    {
        using var dir = new TempDir();
        dir.Write("vest.dat", Dat("Vest", 300, "Armor 0.5\n"));
        dir.Write("pack.dat", Dat("Backpack", 301, "Armor 0.5\n"));

        ClothingArmorDatabase database = ClothingArmorDatabase.ScanDirectory(dir.Path);

        Assert.True(database.IsVest(300));
        Assert.False(database.IsVest(301));
        Assert.False(database.IsVest(999)); // unknown
    }

    [Fact]
    public void TryGet_ReportsWhetherTheIdIsKnown()
    {
        using var dir = new TempDir();
        dir.Write("a.dat", Dat("Hat", 400, "Armor 0.8\n"));
        ClothingArmorDatabase database = ClothingArmorDatabase.ScanDirectory(dir.Path);

        Assert.True(database.TryGet(400, out ClothingArmor hat));
        Assert.Equal(0.8f, hat.Armor);
        Assert.False(database.TryGet(401, out _));
    }

    // Every id is bare against the empty database, which is what a host that never scanned gets — and
    // is the behaviour the whole damage path had before this reader existed.
    [Fact]
    public void Empty_IsAlwaysBare()
    {
        Assert.Equal(1f, ClothingArmorDatabase.Empty.ArmorFor(180));
        Assert.False(ClothingArmorDatabase.Empty.IsVest(180));
    }

    // The real items. The point of the change is that these numbers are on disk.
    [RealDataFact]
    public void RealClothing_CarriesItsArmor()
    {
        ClothingArmorDatabase database = ClothingArmorDatabase.ScanDirectory(
            Path.Combine(GameData.Install!, "Bundles", "Items"));

        Assert.True(database.Count > 100, $"only {database.Count} clothing items were indexed");
        Assert.Equal(0.9f, database.ArmorFor(1011));  // Military_Top_Desert
        Assert.Equal(0.95f, database.ArmorFor(180));  // Tee_White
        Assert.Equal(0.95f, database.ArmorFor(1492)); // Ghillie_Top_Peaks
    }
}

public class ZombieArmorTests
{
    // The table PEI-shaped fixtures use: shirt slot 0, pants slot 1, hat slot 2, gear slot 3.
    private static ZombieTable Table()
    {
        var table = new ZombieTable { Name = "Military", Health = 100, Damage = 15 };
        table.Slots.Add((1f, new List<ushort> { 10, 11 })); // shirts
        table.Slots.Add((1f, new List<ushort> { 20 }));     // pants
        table.Slots.Add((1f, new List<ushort> { 30 }));     // hats
        table.Slots.Add((1f, new List<ushort> { 40, 41 })); // gear: a vest and a backpack
        return table;
    }

    private static ClothingArmorDatabase Clothing()
    {
        var database = new ClothingArmorDatabase();
        Add(database, 10, EClothingType.Shirt, 0.5f);
        Add(database, 11, EClothingType.Shirt, 0.25f);
        Add(database, 20, EClothingType.Pants, 0.8f);
        Add(database, 30, EClothingType.Hat, 0.4f);
        Add(database, 40, EClothingType.Vest, 0.5f);
        Add(database, 41, EClothingType.Backpack, 0.1f); // has armor, but is not a vest
        return database;

        static void Add(ClothingArmorDatabase database, ushort id, EClothingType type, float armor)
        {
            string text = $"GUID 0f1b6a5a8e2c4f0a9c1d2e3f4a5b6c7d\nType {type}\nID {id}\nArmor {armor}\n";
            Assert.True(ClothingArmorDatabase.TryParse(DatParser.Parse(text), out ClothingArmor c));
            database.Add(c);
        }
    }

    private const byte Bare = byte.MaxValue;

    private static float For(ELimb limb, byte shirt = 0, byte pants = 0, byte hat = 0, byte gear = Bare) =>
        ZombieArmor.For(limb, Table(), shirt, pants, hat, gear, Clothing());

    // "if (limb == LEFT_FOOT || LEFT_LEG || RIGHT_FOOT || RIGHT_LEG)" -> the pants.
    [Theory]
    [InlineData(ELimb.LeftFoot)]
    [InlineData(ELimb.LeftLeg)]
    [InlineData(ELimb.RightFoot)]
    [InlineData(ELimb.RightLeg)]
    public void LegsTakeThePants(ELimb limb) => Assert.Equal(0.8f, For(limb));

    // "else if (LEFT_HAND || LEFT_ARM || RIGHT_HAND || RIGHT_ARM)" -> the shirt.
    [Theory]
    [InlineData(ELimb.LeftHand)]
    [InlineData(ELimb.LeftArm)]
    [InlineData(ELimb.RightHand)]
    [InlineData(ELimb.RightArm)]
    public void ArmsTakeTheShirt(ELimb limb) => Assert.Equal(0.5f, For(limb));

    [Fact]
    public void SkullTakesTheHat() => Assert.Equal(0.4f, For(ELimb.Skull));

    // The spine stacks the vest onto the shirt: 0.5 x 0.5.
    [Fact]
    public void SpineMultipliesTheVestByTheShirt() =>
        Assert.Equal(0.25f, For(ELimb.Spine, gear: 0));

    // "asset.type == EItemType.VEST" — the gear slot can hold a backpack, which contributes nothing
    // even though the asset carries an armor value.
    [Fact]
    public void SpineIgnoresGearThatIsNotAVest() =>
        Assert.Equal(0.5f, For(ELimb.Spine, gear: 1));

    [Fact]
    public void SpineWithNoGearIsJustTheShirt() => Assert.Equal(0.5f, For(ELimb.Spine));

    [Fact]
    public void SpineWithNothingAtAllIsBare() =>
        Assert.Equal(1f, For(ELimb.Spine, shirt: Bare, gear: Bare));

    // The clothing roll picks an INDEX into the slot's list, so the second shirt is the second armor.
    [Fact]
    public void TheRolledIndexSelectsTheItem() =>
        Assert.Equal(0.25f, For(ELimb.LeftArm, shirt: 1));

    // 255 is bare, which is what most slots roll.
    [Theory]
    [InlineData(ELimb.LeftArm)]
    [InlineData(ELimb.LeftLeg)]
    [InlineData(ELimb.Skull)]
    public void BareSlotsAreUnarmored(ELimb limb) =>
        Assert.Equal(1f, ZombieArmor.For(limb, Table(), Bare, Bare, Bare, Bare, Clothing()));

    // "zombie.pants < LevelZombies.tables[...].slots[1].table.Count" — an index past the list is bare
    // rather than an exception.
    [Fact]
    public void IndexPastTheSlotList_IsBare() =>
        Assert.Equal(1f, ZombieArmor.For(ELimb.LeftArm, Table(), shirt: 9, pants: 0, hat: 0, gear: Bare,
            Clothing()));

    // A table with fewer slots than the four the lookup names must not throw.
    [Fact]
    public void TableWithNoSlots_IsBare()
    {
        var table = new ZombieTable { Name = "Sparse", Health = 100 };
        Assert.Equal(1f, ZombieArmor.For(ELimb.Spine, table, 0, 0, 0, 0, Clothing()));
        Assert.Equal(1f, ZombieArmor.For(ELimb.LeftArm, table, 0, 0, 0, 0, Clothing()));
    }

    // The quadruped limbs never reach a zombie; the original falls through to 1 for them too.
    [Theory]
    [InlineData(ELimb.LeftBack)]
    [InlineData(ELimb.RightFront)]
    public void QuadrupedLimbsAreBare(ELimb limb) => Assert.Equal(1f, For(limb));

    [Fact]
    public void NoTableOrNoDatabase_IsBare()
    {
        Assert.Equal(1f, ZombieArmor.For(ELimb.Skull, null, 0, 0, 0, 0, Clothing()));
        Assert.Equal(1f, ZombieArmor.For(ELimb.Skull, Table(), 0, 0, 0, 0, null));
    }

    // The end of the point: armor now reaches the punch. A 0.4 hat turns the 45-damage skull hit into
    // 18 rather than leaving it at 45.
    [Fact]
    public void ArmorReachesThePunch()
    {
        float armor = For(ELimb.Skull);
        ushort bare = PunchDamageResolver.Zombie(ELimb.Skull, Godot.Vector3.Right, Godot.Vector3.Forward);
        ushort armored = PunchDamageResolver.Zombie(ELimb.Skull, Godot.Vector3.Right,
            Godot.Vector3.Forward, armor: armor);

        Assert.True(armored < bare, $"armor {armor} did not reduce {bare}");
        Assert.Equal((ushort)(bare * 0.4f), armored);
    }
}
