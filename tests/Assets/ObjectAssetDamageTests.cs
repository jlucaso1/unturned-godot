using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests.Assets;

// The damage numbers as they are actually written in the game's .dat files. Everything the punch does to
// the world is decided by these fields, so this is where "data driven" either is or is not true.
public class ObjectAssetDamageTests
{
    private static ObjectAsset Parse(string text)
    {
        Assert.True(ObjectAsset.TryParse(DatParser.Parse(text), null, out ObjectAsset? asset));
        return asset;
    }

    // Bundles/Trees/Pine_5, verbatim.
    [Fact]
    public void ReadsAResourcesHealth()
    {
        ObjectAsset pine = Parse("""
            GUID 5db018b7fcf04727b072c281d256e06d
            Type Resource
            ID 30

            Health 1200
            Radius 5

            Explosion 99
            Reward_ID 517
            Reward_XP 6

            Reset 750
            """);
        Assert.Equal(EObjectType.Resource, pine.Type);
        Assert.Equal(1200, pine.Health);
        Assert.Equal(0, pine.BladeId);
        // The gate that makes punching a pine do nothing, in the shipped game as much as here.
        Assert.False(pine.VulnerableToFists);
    }

    // Bundles/Trees/Clay_2, which names a blade family — only a tool carrying blade 1 may mine it.
    [Fact]
    public void ReadsAResourcesBladeId()
    {
        ObjectAsset clay = Parse("""
            GUID 40ce1b8f427d4188930df302423f6d1d
            Type Resource
            ID 67

            Health 1400
            BladeID 1
            """);
        Assert.Equal(1400, clay.Health);
        Assert.Equal(1, clay.BladeId);
    }

    [Fact]
    public void ReadsTheFistsOptIn() =>
        Assert.True(Parse("""
            GUID 40ce1b8f427d4188930df302423f6d1d
            Type Resource
            Health 100
            Vulnerable_To_Fists true
            """).VulnerableToFists);

    // A BARE flag is not an opt-in. Unturned reads this field with ParseBool and a false default
    // (ResourceAsset.cs:477), and ParseBool falls back on that default whenever TryParseBool fails —
    // which it does for the null a valueless key holds (DatValueEx.cs:134-160). The booleans Unturned
    // really does write by presence are the ones it reads with ContainsKey instead
    // (ResourceAsset.cs:483, 506; ObjectAsset.cs:1154, 1165-1167). This one is spelled out or it is off.
    [Fact]
    public void ABareFlagIsNotAnOptIn() =>
        Assert.False(Parse("""
            GUID 40ce1b8f427d4188930df302423f6d1d
            Type Resource
            Health 100
            Vulnerable_To_Fists
            """).VulnerableToFists);

    // The spellings DatValueEx.TryParseBool accepts as a single character, which bool.TryParse does not.
    [Theory]
    [InlineData("y", true)]
    [InlineData("t", true)]
    [InlineData("1", true)]
    [InlineData("n", false)]
    [InlineData("f", false)]
    [InlineData("0", false)]
    public void ReadsTheSingleLetterSpellings(string raw, bool expected) =>
        Assert.Equal(expected, Parse($"""
            GUID 40ce1b8f427d4188930df302423f6d1d
            Type Resource
            Health 100
            Vulnerable_To_Fists {raw}
            """).VulnerableToFists);

    [Fact]
    public void AnExplicitFalseIsFalse() =>
        Assert.False(Parse("""
            GUID 40ce1b8f427d4188930df302423f6d1d
            Type Resource
            Health 100
            Vulnerable_To_Fists False
            """).VulnerableToFists);

    // Bundles/Objects/Medium/Rubble/Garbage_1, verbatim: the destructible prop a fist CAN break.
    [Fact]
    public void ReadsARubbleBlock()
    {
        ObjectAsset garbage = Parse("""
            GUID a19b3ec55a2046668611c9d2775efd99
            Type Medium
            ID 60

            Rubble Destroy
            Rubble_Reset 300
            Rubble_Health 75
            Rubble_Effect 70
            """);
        Assert.True(garbage.HasRubble);
        Assert.Equal(75, garbage.RubbleHealth);
        Assert.Equal(0, garbage.RubbleBladeId);
        Assert.True(garbage.RubbleIsVulnerable);
    }

    // The same fields under the other prefix, which is how an object whose interactability IS rubble
    // writes them.
    [Fact]
    public void ReadsTheInteractabilitySpelling()
    {
        ObjectAsset asset = Parse("""
            GUID a19b3ec55a2046668611c9d2775efd99
            Type Large
            Interactability Rubble
            Interactability_Health 250
            Interactability_Blade_ID 2
            """);
        Assert.True(asset.HasRubble);
        Assert.Equal(250, asset.RubbleHealth);
        Assert.Equal(2, asset.RubbleBladeId);
    }

    // rubbleIsVulnerable is the NEGATION of a bare flag.
    [Fact]
    public void InvulnerableRubbleIsNotVulnerable()
    {
        ObjectAsset asset = Parse("""
            GUID a19b3ec55a2046668611c9d2775efd99
            Type Medium
            Rubble Destroy
            Rubble_Health 75
            Rubble_Invulnerable
            """);
        Assert.True(asset.HasRubble);
        Assert.False(asset.RubbleIsVulnerable);
    }

    // An ordinary prop has no rubble block at all, which is a different thing from having an
    // invulnerable one: it is not destructible, so it never becomes a target.
    [Fact]
    public void AnOrdinaryObjectHasNoRubble()
    {
        ObjectAsset wall = Parse("""
            GUID a19b3ec55a2046668611c9d2775efd99
            Type Large
            ID 5
            """);
        Assert.False(wall.HasRubble);
        Assert.False(wall.RubbleIsVulnerable);
        Assert.Equal(0, wall.RubbleHealth);
        Assert.Equal(0, wall.Health);
    }

    // ---- The real files ---------------------------------------------------------------------------

    // Every shipped tree carries health, and none of them is vulnerable to fists — so punching a tree in
    // this port does exactly what it does in the game: nothing at all. If a future content update ever
    // flips one of them, this is what says so before a player wonders why the pine fell.
    [RealDataFact]
    public void NoShippedTreeIsVulnerableToFists()
    {
        string trees = Path.Combine(Helpers.GameData.Install!, "Bundles", "Trees");
        Assert.True(Directory.Exists(trees), $"{trees} is missing from the install");

        int resources = 0;
        foreach (string file in Directory.EnumerateFiles(trees, "*.dat", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).Contains("English", System.StringComparison.OrdinalIgnoreCase))
                continue;
            if (!ObjectAsset.TryParse(DatParser.Parse(File.ReadAllText(file)), null,
                    out ObjectAsset? asset) || asset.Type != EObjectType.Resource)
                continue;
            resources++;
            Assert.False(asset.VulnerableToFists,
                $"{Path.GetFileName(file)} opts into fist damage; the punch table would now fell it");
        }
        Assert.True(resources > 50, $"only {resources} resources parsed out of {trees}");
    }

    // And the rubble props are the punchable half of the world: they carry health, want no blade, and
    // are not invulnerable — so a fist really does break the garbage piles the maps scatter.
    [RealDataFact]
    public void ShippedRubblePropsArePunchable()
    {
        string rubble = Path.Combine(Helpers.GameData.Install!, "Bundles", "Objects", "Medium", "Rubble");
        Assert.True(Directory.Exists(rubble), $"{rubble} is missing from the install");

        int punchable = 0;
        foreach (string file in Directory.EnumerateFiles(rubble, "*.dat", SearchOption.AllDirectories))
        {
            if (!ObjectAsset.TryParse(DatParser.Parse(File.ReadAllText(file)), null,
                    out ObjectAsset? asset) || !asset.HasRubble)
                continue;
            if (asset.RubbleHealth > 0 && asset.RubbleBladeId == 0 && asset.RubbleIsVulnerable)
                punchable++;
        }
        Assert.True(punchable > 0, $"no punchable rubble found under {rubble}");
    }
}
