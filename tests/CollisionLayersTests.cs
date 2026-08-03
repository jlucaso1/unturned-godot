using System.Collections.Generic;
using Xunit;

namespace UnturnedGodot.Tests;

// The layer layout used to live as bare numbers across three files in src/, which is excluded from
// coverage — so nothing could state, let alone check, that two roles must not share a bit. These are the
// invariants the comments at those sites already claimed.
public class CollisionLayersTests
{
    private static readonly (string Name, uint Bit)[] Roles =
    {
        ("World", CollisionLayers.World),
        ("VisionBlocker", CollisionLayers.VisionBlocker),
        ("MediumFurniture", CollisionLayers.MediumFurniture),
        ("Player", CollisionLayers.Player),
    };

    // The bug this file exists to prevent: the player sat on VisionBlocker, so the zombie alert raycast —
    // which masks exactly that bit and stops at 95% of the way to its target — ended inside the player's
    // own capsule at close range and reported vision blocked. Zombies went blind to nearby players.
    [Fact]
    public void ThePlayerIsNotAVisionBlocker()
    {
        Assert.Equal(0u, CollisionLayers.Player & CollisionLayers.VisionBlocker);
    }

    [Fact]
    public void EveryRoleHasItsOwnBit()
    {
        var seen = new Dictionary<uint, string>();
        foreach ((string name, uint bit) in Roles)
        {
            Assert.True(seen.TryAdd(bit, name),
                $"{name} shares bit {bit} with {(seen.TryGetValue(bit, out string? other) ? other : "?")}");
        }
    }

    [Theory]
    [InlineData("World")]
    [InlineData("VisionBlocker")]
    [InlineData("MediumFurniture")]
    [InlineData("Player")]
    public void EveryRoleIsASingleBit(string name)
    {
        uint bit = System.Array.Find(Roles, r => r.Name == name).Bit;

        Assert.NotEqual(0u, bit);
        Assert.Equal(0u, bit & (bit - 1)); // a power of two
    }

    // A character must collide with the solid world and with furniture, and must not be masked into the
    // vision query or into itself.
    [Fact]
    public void TheCharacterMaskIsTheWorldAndFurnitureOnly()
    {
        Assert.Equal(CollisionLayers.World | CollisionLayers.MediumFurniture, CollisionLayers.CharacterMask);
        Assert.Equal(0u, CollisionLayers.CharacterMask & CollisionLayers.Player);
        Assert.Equal(0u, CollisionLayers.CharacterMask & CollisionLayers.VisionBlocker);
    }
}
