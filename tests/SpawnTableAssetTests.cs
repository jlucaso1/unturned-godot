using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class SpawnTableAssetTests
{
    private static SpawnTableAsset Parse(string text)
    {
        Assert.True(SpawnTableAsset.TryParse(DatParser.Parse(text), out SpawnTableAsset? asset));
        return asset;
    }

    [Fact]
    public void TryParse_ReadsTheIndexedLegacyLayout()
    {
        SpawnTableAsset table = Parse("""
            GUID 7e642d28677240ea8147841dbaeb0704
            Type Spawn
            ID 296

            Tables 3
            Table_0_Spawn_ID 297
            Table_0_Weight 30
            Table_1_Asset_ID 185
            Table_1_Weight 50
            Table_2_GUID 20cdf1b0b0324866be88d5fb7a5c4507
            Table_2_Weight 20
            """);

        Assert.Equal((ushort)296, table.Id);
        Assert.Equal(3, table.Entries.Count);
        // Sorted heaviest first, and the cumulative ladder ends at 1.
        Assert.Equal(50, table.Entries[0].Weight);
        Assert.Equal(0.5f, table.CumulativeWeights[0], 5);
        Assert.Equal(1.0f, table.CumulativeWeights[2], 5);
    }

    [Fact]
    public void TryParse_ReadsTheListLayoutAndDropsInvalidEntries()
    {
        SpawnTableAsset table = Parse("""
            Metadata
            {
                GUID 7e642d28677240ea8147841dbaeb0704
                Type SDG.Unturned.SpawnAsset, Assembly-CSharp
            }
            Asset
            {
                ID 296
                Tables
                [
                    {
                        LegacyAssetId 12
                        Weight 5
                    }
                    {
                        LegacyAssetId 13
                        Weight 0
                    }
                    {
                        Weight 9
                    }
                ]
            }
            """);

        // A weightless entry and one naming nothing are both rejected, exactly as Unturned rejects them.
        SpawnTableAsset.Entry entry = Assert.Single(table.Entries);
        Assert.Equal((ushort)12, entry.AssetId);
        Assert.Equal(5, entry.Weight);
    }

    [Fact]
    public void TryParse_RejectsAssetsThatAreNotSpawnTables()
    {
        Assert.False(SpawnTableAsset.TryParse(
            DatParser.Parse("GUID 7e642d28677240ea8147841dbaeb0704\nType Vehicle\nID 58\n"), out _));
    }

    [Fact]
    public void Pick_LandsOnTheEntryTheRollFallsUnder()
    {
        SpawnTableAsset table = Parse("""
            GUID 7e642d28677240ea8147841dbaeb0704
            Type Spawn
            Tables 2
            Table_0_Asset_ID 1
            Table_0_Weight 75
            Table_1_Asset_ID 2
            Table_1_Weight 25
            """);

        Assert.Equal((ushort)1, table.Pick(0f)!.Value.AssetId);
        Assert.Equal((ushort)1, table.Pick(0.74f)!.Value.AssetId);
        Assert.Equal((ushort)2, table.Pick(0.76f)!.Value.AssetId);
        // A roll at or past the end still resolves: the last entry always answers.
        Assert.Equal((ushort)2, table.Pick(1f)!.Value.AssetId);
    }

    [Fact]
    public void Resolve_DescendsThroughNestedTablesToTheAsset()
    {
        SpawnTableDatabase db = ScanTree(
            ("PEI_Civilian", "GUID 20cdf1b0b0324866be88d5fb7a5c4507\nType Spawn\nID 295\n"
                + "Tables 1\nTable_0_Spawn_ID 296\nTable_0_Weight 100\n"),
            ("Civilian", "GUID 7e642d28677240ea8147841dbaeb0704\nType Spawn\nID 296\n"
                + "Tables 1\nTable_0_Asset_ID 185\nTable_0_Weight 100\n"));

        (Guid guid, ushort assetId) = db.Resolve(db.ById(295)!, () => 0f);

        Assert.Equal(Guid.Empty, guid);
        Assert.Equal((ushort)185, assetId);
    }

    [Fact]
    public void Resolve_FollowsAGuidThatNamesAnotherTableAndReturnsOneThatDoesNot()
    {
        SpawnTableDatabase db = ScanTree(
            ("Root", "GUID 20cdf1b0b0324866be88d5fb7a5c4507\nType Spawn\nID 1\n"
                + "Tables 1\nTable_0_GUID 7e642d28677240ea8147841dbaeb0704\nTable_0_Weight 100\n"),
            ("Nested", "GUID 7e642d28677240ea8147841dbaeb0704\nType Spawn\nID 2\n"
                + "Tables 1\nTable_0_GUID 33333333333333333333333333333333\nTable_0_Weight 100\n"));

        (Guid guid, ushort assetId) = db.Resolve(db.ById(1)!, () => 0f);

        Assert.Equal(Guid.Parse("33333333333333333333333333333333"), guid);
        Assert.Equal((ushort)0, assetId);
    }

    [Fact]
    public void Resolve_GivesUpOnATableThatNamesItself()
    {
        SpawnTableDatabase db = ScanTree(
            ("Loop", "GUID 20cdf1b0b0324866be88d5fb7a5c4507\nType Spawn\nID 1\n"
                + "Tables 1\nTable_0_Spawn_ID 1\nTable_0_Weight 100\n"));

        Assert.Equal((Guid.Empty, (ushort)0), db.Resolve(db.ById(1)!, () => 0f));
    }

    [Fact]
    public void Resolve_ReturnsNothingForAnEmptyTable()
    {
        SpawnTableDatabase db = ScanTree(
            ("Empty", "GUID 20cdf1b0b0324866be88d5fb7a5c4507\nType Spawn\nID 1\nTables 0\n"));

        Assert.Equal((Guid.Empty, (ushort)0), db.Resolve(db.ById(1)!, () => 0f));
    }

    [Fact]
    public void ScanDirectory_SkipsAnAbsentTreeAndKeepsTheFirstClaimOfAnId()
    {
        Assert.Equal(0, SpawnTableDatabase.ScanDirectory(Path.Combine(Path.GetTempPath(), "no-such-tree")).Count);

        var db = new SpawnTableDatabase();
        db.AddIfAbsent(Parse("GUID 11111111111111111111111111111111\nType Spawn\nID 7\nTables 0\n"));
        db.AddIfAbsent(Parse("GUID 22222222222222222222222222222222\nType Spawn\nID 7\nTables 0\n"));

        Assert.Equal(Guid.Parse("11111111111111111111111111111111"), db.ById(7)!.Guid);
        Assert.Equal(2, db.Count); // both GUIDs are indexed; only the id is contested
    }

    [Fact]
    public void ScanDirectory_IndexesTablesAndSkipsWhatIsNotOne()
    {
        using var dir = new TempDir();
        dir.Write("Vehicles/Civilian/Civilian.dat", "GUID 7e642d28677240ea8147841dbaeb0704\nType Spawn\n"
            + "ID 296\nTables 1\nTable_0_Asset_ID 40\nTable_0_Weight 100\n");
        dir.Write("Vehicles/Civilian/English.dat", "Name Civilian\n"); // no GUID: not a table
        dir.Write("Vehicles/Notes.txt", "ignored\n");
        // The newer layout ships as ".asset"; the game's own fishing tables are written this way.
        dir.Write("Vehicles/Rare/Rare.asset", """
            GUID 20cdf1b0b0324866be88d5fb7a5c4507
            Type Spawn
            ID 297
            Tables
            [
                {
                    Guid 8c32debcf1974cd7a24a46779872c205
                    Weight 120
                }
            ]
            """);
        // A dangling symlink named .dat is enumerated but unreadable; it must not cost the whole scan.
        File.CreateSymbolicLink(Path.Combine(dir.Path, "Vehicles", "broken.dat"),
            Path.Combine(dir.Path, "absent.dat"));

        SpawnTableDatabase db = SpawnTableDatabase.ScanDirectory(dir.Path);

        Assert.Equal(2, db.Count);
        SpawnTableAsset table = db.ById(296)!;
        Assert.NotNull(table);
        Assert.Same(table, db.ByGuid(table.Guid));
        Assert.Equal(120, Assert.Single(db.ById(297)!.Entries).Weight);
        Assert.Null(db.ByGuid(Guid.Empty));
        Assert.Null(db.ById(0));
    }

    [RealDataFact]
    public void ScanDirectory_ReadsTheGamesVehicleSpawnTables()
    {
        SpawnTableDatabase db = SpawnTableDatabase.ScanDirectory(
            Path.Combine(GameData.Install!, "Bundles", "Spawns", "Vehicles"));

        Assert.True(db.Count > 100, $"expected the game's vehicle tables, found {db.Count}");
        // Every legacy spawn id a vehicle table references resolves inside the same subtree, which is
        // what lets the vehicle path scan only this one.
        foreach (SpawnTableAsset table in db.All)
            foreach (SpawnTableAsset.Entry entry in table.Entries)
                if (entry.SpawnId != 0)
                    Assert.NotNull(db.ById(entry.SpawnId));
    }

    private static SpawnTableDatabase ScanTree(params (string Name, string Text)[] tables)
    {
        var db = new SpawnTableDatabase();
        foreach ((string name, string text) in tables)
            db.AddIfAbsent(Parse(text + $"// {name}\n"));
        return db;
    }
}
