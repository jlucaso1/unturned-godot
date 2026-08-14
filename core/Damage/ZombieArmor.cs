using System.Collections.Generic;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;

namespace UnturnedGodot.Damage;

// DamageTool.getZombieArmor (DamageTool.cs:644-713), ported limb for limb.
//
// The zombie already carries everything this needs: ZombieManager's clothing roll picked an INDEX into
// each slot's item list at spawn, the port already reproduces that roll (ZombieSystem.RollSlot) and
// already replicates the four indices. What was missing was the last hop — index into the table's item
// list, item id into ItemClothingAsset, asset into `armor` — because nothing read clothing assets. That
// is what ClothingArmorDatabase supplies, and this turns it into the multiplier the punch applies.
//
// Every failure along the way returns 1 (bare), which is the original's behaviour too: it tests the
// index against the slot list's Count and null-checks the asset lookup at every step, and most zombies
// roll empty slots anyway.
public static class ZombieArmor
{
    // Slot indices, from getZombieArmor's own comment: "0 Shirt / 1 Pants / 2 Hat / 3 Gear".
    private const int Shirt = 0, Pants = 1, Hat = 2, Gear = 3;

    // `table` is the zombie's own ZombieTable and the four indices are what it rolled, 255 meaning bare.
    public static float For(ELimb limb, ZombieTable? table, byte shirt, byte pants, byte hat, byte gear,
        ClothingArmorDatabase? clothing)
    {
        if (table == null || clothing == null)
            return 1f;

        switch (limb)
        {
            // Legs and feet take the PANTS.
            case ELimb.LeftFoot or ELimb.LeftLeg or ELimb.RightFoot or ELimb.RightLeg:
                return Armor(table, Pants, pants, clothing);

            // Arms and hands take the SHIRT.
            case ELimb.LeftHand or ELimb.LeftArm or ELimb.RightHand or ELimb.RightArm:
                return Armor(table, Shirt, shirt, clothing);

            // The spine is the one limb that stacks two garments — and the gear slot only counts when
            // what it rolled is a VEST ("asset.type == EItemType.VEST"), because the slot can hold a
            // backpack or a mask just as well.
            case ELimb.Spine:
                {
                    float armor = 1f;
                    if (Item(table, Gear, gear) is { } gearId && clothing.IsVest(gearId))
                        armor *= clothing.ArmorFor(gearId);
                    if (Item(table, Shirt, shirt) is { } shirtId)
                        armor *= clothing.ArmorFor(shirtId);
                    return armor;
                }

            // The skull takes the HAT.
            case ELimb.Skull:
                return Armor(table, Hat, hat, clothing);

            // The quadruped limbs never reach a zombie; the original falls through to 1 as well.
            default:
                return 1f;
        }
    }

    private static float Armor(ZombieTable table, int slot, byte index, ClothingArmorDatabase clothing) =>
        Item(table, slot, index) is { } id ? clothing.ArmorFor(id) : 1f;

    // "zombie.pants != 255 && zombie.pants < LevelZombies.tables[...].slots[1].table.Count" — the 255
    // guard and the bounds check, in one place because all four slots make exactly the same two tests.
    private static ushort? Item(ZombieTable table, int slot, byte index)
    {
        if (index == byte.MaxValue || slot >= table.Slots.Count)
            return null;
        List<ushort> items = table.Slots[slot].Items;
        return index < items.Count ? items[index] : null;
    }
}
