using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

// The four placement-fidelity findings, measured against the bytes PEI actually ships rather than
// against a fixture. Each number below was counted off the real files before the fix existed, and the
// point of asserting it here is that a regression has to move a number somebody can go and check.
[Trait("Category", "RealData")]
public class PlacementFidelityRealDataTests
{
    // The whole asset database is a scan of thousands of .dat files; one per run, shared.
    //
    // THE GAME'S OWN CONTENT ONLY. Every count below is a claim about what Unturned ships — "109 object
    // assets carry a holiday restriction" is checkable precisely because it is a fact about the depot.
    // ContentSource.Discover also returns the workshop items this machine happens to be subscribed to,
    // and scanning those makes each number a fact about the developer's Steam account instead: a clean
    // CI runner counted 9 redirecting assets and a machine with six subscriptions counted 26, from the
    // same commit. Production scans everything, which is the point of mods; a test that pins a number
    // has to scan the thing the number is about.
    private static readonly Lazy<ObjectAssetDatabase> Assets = new(() =>
        ContentExtraction.ScanAssets(GameData.CoreSources()));

    private static List<PlacedObject> PeiObjects() =>
        LevelObjects.Load(Path.Combine(GameData.Map("PEI")!, "Level", "Objects.dat"));

    private static List<PlacedTree> PeiTrees() =>
        LevelTrees.Load(Path.Combine(GameData.Map("PEI")!, "Terrain", "Trees.dat"));

    // --- Finding 1: every tree was 0.75 m too high ---

    [RealDataFact(Map = "PEI")]
    public void Trees_AreSunkByTheirAssetsVerticalOffset()
    {
        List<PlacedTree> trees = PeiTrees();
        ObjectAssetDatabase db = Assets.Value;
        var placements = new List<PlacedObject>();

        // Christmas is forced on so the 82 holiday-restricted trees stay in the list and the two sides
        // line up index for index; the offset is what this test is about, not the restriction.
        LegacyPlacements.AppendTrees(trees, placements, db, new HolidayPolicy(ENPCHoliday.Christmas, false));

        Assert.Equal(1694, trees.Count);
        Assert.Equal(trees.Count, placements.Count);

        int sunk = 0, lifted = 0;
        for (int i = 0; i < trees.Count; i++)
        {
            ObjectAsset asset = db.ResolveByGuid(trees[i].Guid)!;
            Assert.NotNull(asset);
            float expected = trees[i].Position.Y + (trees[i].Scale.Y * asset.VerticalOffset);
            Assert.Equal(expected, placements[i].Position.Y, 3);
            // Untouched on the axes the offset does not act on.
            Assert.Equal(trees[i].Position.X, placements[i].Position.X);
            Assert.Equal(trees[i].Position.Z, placements[i].Position.Z);

            if (trees[i].Scale.Y != 1f)
                continue;

            if (asset.VerticalOffset == ObjectAsset.DefaultVerticalOffset)
            {
                sunk++;
                // The headline: 0.75 m, straight down, on every one of them.
                Assert.Equal(trees[i].Position.Y - 0.75f, placements[i].Position.Y, 3);
            }
            else
            {
                lifted++;
                Assert.Equal(trees[i].Position.Y + 0.1f, placements[i].Position.Y, 3);
            }
        }

        // 1,615 of PEI's 1,694 trees stand at exactly unit scale — every one of them was drawn a flat
        // 0.75 m off. The split is the argument for reading the field instead of hardcoding the default:
        // 13 of those are the mushrooms, which the game LIFTS by 0.1, and a constant would have planted
        // them 0.85 m underground.
        Assert.Equal(1602, sunk);
        Assert.Equal(13, lifted);
        Assert.Equal(1615, sunk + lifted);
    }

    [RealDataFact(Map = "PEI")]
    public void EightyTwoOfPeisTreesAreChristmasPropsToo()
    {
        // Beyond the 285 restricted OBJECTS: ResourceSpawnpoint.cs:539 gates trees on the same key, and
        // PEI plants 82 of them — snow piles, ice, candy canes, ornaments. They were drawn, and walked
        // into, in August as well.
        ObjectAssetDatabase db = Assets.Value;
        List<PlacedTree> trees = PeiTrees();

        int restricted = 0;
        foreach (PlacedTree t in trees)
            if (db.ResolveByGuid(t.Guid)?.HolidayRestriction == ENPCHoliday.Christmas)
                restricted++;
        Assert.Equal(82, restricted);

        var summer = new List<PlacedObject>();
        LegacyPlacements.AppendTrees(trees, summer, db, HolidayPolicy.None);
        Assert.Equal(1694 - 82, summer.Count);

        var december = new List<PlacedObject>();
        LegacyPlacements.AppendTrees(trees, december, db, new HolidayPolicy(ENPCHoliday.Christmas, false));
        Assert.Equal(1694, december.Count);
    }

    [RealDataFact(Map = "PEI")]
    public void TreeAssets_TwoOfThemOverrideTheDefaultOffset()
    {
        // Which is why the constant alone would not have been enough: the two mushrooms are LIFTED.
        var offsets = new List<float>();
        int treeAssets = 0;
        foreach (ObjectAsset asset in Assets.Value.All)
        {
            if (asset.Type != EObjectType.Resource || !asset.Directory.Contains("Trees", StringComparison.Ordinal))
                continue;
            treeAssets++;
            if (asset.VerticalOffset != ObjectAsset.DefaultVerticalOffset)
                offsets.Add(asset.VerticalOffset);
        }

        Assert.Equal(69, treeAssets);
        Assert.Equal(2, offsets.Count);
        Assert.All(offsets, o => Assert.Equal(0.1f, o, 4));
    }

    // --- Finding 2: 285 holiday props drawn and collidable in August ---

    [RealDataFact(Map = "PEI")]
    public void HolidayProps_AreHiddenOutOfSeasonAndRestoredInIt()
    {
        List<PlacedObject> all = PeiObjects();
        ObjectAssetDatabase db = Assets.Value;
        Assert.Equal(4329, all.Count);

        // Counted straight off the placements, independent of the loader under test.
        int christmas = 0, halloween = 0;
        foreach (PlacedObject o in all)
        {
            switch (db.ResolveByGuid(o.Guid)?.HolidayRestriction)
            {
                case ENPCHoliday.Christmas: christmas++; break;
                case ENPCHoliday.Halloween: halloween++; break;
                default: break;
            }
        }
        Assert.Equal(254, christmas);
        Assert.Equal(31, halloween);

        var outOfSeason = PeiObjects();
        LegacyPlacements.ResolveGuids(outOfSeason, db, HolidayPolicy.None);
        Assert.Equal(4329 - 285, outOfSeason.Count);

        // In December the Christmas ones come back and the Halloween ones stay gone: one holiday runs at
        // a time. Substitution is left off here so this measures the restriction gate alone.
        var december = PeiObjects();
        LegacyPlacements.ResolveGuids(december, db, new HolidayPolicy(ENPCHoliday.Christmas, false));
        Assert.Equal(4329 - 31, december.Count);
    }

    [RealDataFact(Map = "PEI")]
    public void HolidayRestriction_IsCarriedBy109ObjectAssets()
    {
        int restricted = 0, redirecting = 0;
        foreach (ObjectAsset asset in Assets.Value.All)
        {
            if (asset.Type == EObjectType.Resource || asset.Type == EObjectType.Vehicle
                || asset.Type == EObjectType.VehicleRedirector)
            {
                continue;
            }
            if (asset.HolidayRestriction != ENPCHoliday.None)
                restricted++;
            if (asset.ChristmasRedirect != Guid.Empty || asset.HalloweenRedirect != Guid.Empty)
                redirecting++;
        }

        Assert.Equal(109, restricted);
        Assert.Equal(9, redirecting);
    }

    [RealDataFact(Map = "PEI")]
    public void ThirtyTwoOfTheSixtyNineTreeAssetsRedirectForAHoliday()
    {
        int redirecting = 0;
        foreach (ObjectAsset asset in Assets.Value.All)
        {
            if (asset.Type == EObjectType.Resource
                && asset.Directory.Contains("Trees", StringComparison.Ordinal)
                && (asset.ChristmasRedirect != Guid.Empty || asset.HalloweenRedirect != Guid.Empty))
            {
                redirecting++;
            }
        }

        Assert.Equal(32, redirecting);
    }

    [RealDataFact(Map = "PEI")]
    public void TheTreeListItselfChangesShapeAtChristmas()
    {
        // LevelGround builds a TreeRedirectorMap and discards any tree whose redirect resolves to
        // nothing, so December is not merely a re-skin of the same forest.
        ObjectAssetDatabase db = Assets.Value;
        var summer = new List<PlacedObject>();
        var winter = new List<PlacedObject>();
        List<PlacedTree> trees = PeiTrees();

        LegacyPlacements.AppendTrees(trees, summer, db, HolidayPolicy.None);
        LegacyPlacements.AppendTrees(trees, winter, db,
            new HolidayPolicy(ENPCHoliday.Christmas, allowRedirects: true));

        var summerGuids = new HashSet<Guid>();
        foreach (PlacedObject o in summer)
            summerGuids.Add(o.Guid);
        var winterGuids = new HashSet<Guid>();
        foreach (PlacedObject o in winter)
            winterGuids.Add(o.Guid);

        // Whatever the install has, the two seasons must not resolve to the same set of tree assets.
        Assert.NotEqual(summerGuids, winterGuids);
        Assert.All(winter, o => Assert.NotEqual(Guid.Empty, o.Guid));
    }

    // --- Finding 4: the three latent divergences ---

    [RealDataFact(Map = "PEI")]
    public void ObjectRounding_ChangesNothingOnPei()
    {
        // Latent by construction: the modern editor already writes rounded transforms, so all 4,329 of
        // PEI's placements survive the snap untouched. If this ever fails, the rounding is too eager.
        foreach (PlacedObject o in PeiObjects())
        {
            Assert.Equal(o.EulerDegrees, UnityRounding.RoundIfNearlyAxisAligned(o.EulerDegrees));
            Assert.Equal(o.Scale, UnityRounding.RoundIfNearlyEqualToOne(o.Scale));
        }
    }

    [RealDataFact(Map = "PEI")]
    public void Lighting_IsVersion12SoTheFogClampDoesNotApply()
    {
        string path = Path.Combine(GameData.Map("PEI")!, "Environment", "Lighting.dat");
        LevelLighting lighting = LevelLighting.Load(path)!;

        Assert.Equal(12, lighting.Version);
        // Above the 0.33 ceiling somewhere in the cycle, which is what makes "version 12 is exempt" a
        // claim with teeth rather than a coincidence of small numbers.
        bool anyAboveTheLegacyCap = false;
        foreach (LightingKeyframe k in lighting.Times)
            anyAboveTheLegacyCap |= k.FogDensity > 0.33f;
        Assert.True(anyAboveTheLegacyCap,
            "PEI has no keyframe above 0.33, so this test can no longer tell the clamp apart");
    }

    [RealDataFact(Map = "PEI")]
    public void Roads_AllTwentyThreeStillUseTheLegacyTable()
    {
        List<PlacedRoad> roads = LevelRoads.LoadPaths(
            Path.Combine(GameData.Map("PEI")!, "Environment", "Paths.dat"));

        Assert.Equal(23, roads.Count);
        // Empty means "use Roads.dat's entry `Material`", which is what this port does — so the GUID
        // being carried changes nothing here and everything on a map that migrated.
        Assert.All(roads, r => Assert.Equal(Guid.Empty, r.RoadAssetGuid));
    }

    [RealDataFact(Map = "PEI")]
    public void TreesDat_IsVersion8SoNothingIsJittered()
    {
        // PEI bakes rotation and scale into the file, so GetLegacyRotationAndScale must not touch it;
        // the jitter is for Alpha Valley (6) and Monolith (5), which the fetch script does not pull.
        List<PlacedTree> trees = PeiTrees();

        Assert.All(trees, t => Assert.False(t.NeedsLegacyRotationAndScale));

        var placements = new List<PlacedObject>();
        LegacyPlacements.AppendTrees(trees, placements, Assets.Value,
            new HolidayPolicy(ENPCHoliday.Christmas, false)); // keeps all 1,694, so the indices line up
        Assert.Equal(trees.Count, placements.Count);
        for (int i = 0; i < trees.Count; i++)
        {
            Assert.Equal(trees[i].EulerDegrees, placements[i].EulerDegrees);
            Assert.Equal(trees[i].Scale, placements[i].Scale);
        }
    }
}
