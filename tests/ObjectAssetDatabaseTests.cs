using System;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class ObjectAssetDatabaseTests
{
    private static ObjectAsset Make(string guid, ushort id, string type)
    {
        DatDictionary root = DatParser.Parse($"GUID {guid}\nType {type}\nID {id}\n");
        Assert.True(ObjectAsset.TryParse(root, null, out var asset));
        return asset;
    }

    [Fact]
    public void ScanDirectory_IndexesAssets_AndReadsLocalizedName()
    {
        using var dir = new TempDir();
        dir.Write("Cardboard/Cardboard.dat", "GUID 2e698a7b85e94c019b3f91ec8796a961\nType Small\nID 57\n");
        dir.Write("Cardboard/English.dat", "Name Cardboard #1\n");
        dir.Write("Cardboard/README.txt", "ignored\n");
        // A .dat with no GUID (e.g. a stray localization file) is skipped.
        dir.Write("Loose/French.dat", "Name Carton\n");

        ObjectAssetDatabase db = ObjectAssetDatabase.ScanDirectory(dir.Path);

        Assert.Equal(1, db.Count);
        ObjectAsset? asset = db.ResolveById(57);
        Assert.NotNull(asset);
        Assert.Equal("Cardboard #1", asset!.Name);
    }

    [Fact]
    public void ScanDirectory_RecordsDirectory_AndExposesAll()
    {
        using var dir = new TempDir();
        dir.Write("Cardboard/Cardboard.dat", "GUID 2e698a7b85e94c019b3f91ec8796a961\nType Small\nID 7\n");

        ObjectAssetDatabase db = ObjectAssetDatabase.ScanDirectory(dir.Path);
        ObjectAsset asset = Assert.Single(db.All);
        Assert.EndsWith("Cardboard", asset.Directory);
    }

    [Fact]
    public void ScanDirectory_SkipsDatWithoutGuid()
    {
        using var dir = new TempDir();
        dir.Write("Loc/English.dat", "Name Something\n"); // no GUID -> not an object asset
        Assert.Equal(0, ObjectAssetDatabase.ScanDirectory(dir.Path).Count);
    }

    [Fact]
    public void ScanDirectory_MissingRoot_ReturnsEmpty()
    {
        Assert.Equal(0, ObjectAssetDatabase.ScanDirectory("/no/such/dir").Count);
    }

    [Fact]
    public void ScanDirectory_UnreadableFile_IsSkipped()
    {
        using var dir = new TempDir();
        dir.Write("Real/Real.dat", "GUID 2e698a7b85e94c019b3f91ec8796a961\nType Small\nID 1\n");
        // Dangling symlink with a .dat name: enumerated, but ReadAllText throws (IOException subclass).
        string link = Path.Combine(dir.Path, "broken.dat");
        File.CreateSymbolicLink(link, Path.Combine(dir.Path, "does_not_exist.dat"));

        ObjectAssetDatabase db = ObjectAssetDatabase.ScanDirectory(dir.Path);
        Assert.Equal(1, db.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScanFiles_PerFileIoOrAccessFailureDoesNotEscapeParallelScan(bool unauthorized)
    {
        string good = Path.Combine(Path.GetTempPath(), "good.dat");
        string bad = Path.Combine(Path.GetTempPath(), "bad.dat");
        ObjectAssetDatabase db = ObjectAssetDatabase.ScanFiles(new[] { good, bad }, path =>
        {
            if (path == bad)
                throw unauthorized ? new UnauthorizedAccessException() : new IOException();
            return "GUID 2e698a7b85e94c019b3f91ec8796a961\nType Small\nID 1\n";
        });

        Assert.Equal(1, db.Count);
        Assert.NotNull(db.ResolveById(1));
    }

    [Fact]
    public void Resolve_GuidWins_ThenFallsBackToId()
    {
        var db = new ObjectAssetDatabase();
        ObjectAsset byGuid = Make("2e698a7b85e94c019b3f91ec8796a961", 10, "Small");
        ObjectAsset byId = Make("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 20, "Large");
        db.Add(byGuid);
        db.Add(byId);

        Assert.Equal(byGuid.Guid, db.Resolve(byGuid.Guid, 999)!.Guid);           // guid hit
        Assert.Equal(20, db.Resolve(Guid.NewGuid(), 20)!.Id);                    // guid miss -> id fallback
        Assert.Equal(20, db.Resolve(Guid.Empty, 20)!.Id);                        // no guid -> id
        Assert.Null(db.Resolve(Guid.NewGuid(), 0));                              // nothing to resolve
    }

    [Fact]
    public void ResolveById_Zero_IsNull()
    {
        var db = new ObjectAssetDatabase();
        Assert.Null(db.ResolveById(0));
    }

    [Fact]
    public void ResolveById_UnknownNonZero_IsNull()
    {
        Assert.Null(new ObjectAssetDatabase().ResolveById(999));
    }

    [Fact]
    public void Add_AssetWithoutId_NotIndexedById()
    {
        DatDictionary root = DatParser.Parse("GUID 2e698a7b85e94c019b3f91ec8796a961\nType Small\n"); // no ID
        Assert.True(ObjectAsset.TryParse(root, null, out var asset));
        Assert.Equal(0, asset.Id);

        var db = new ObjectAssetDatabase();
        db.Add(asset);
        Assert.NotNull(db.ResolveByGuid(asset.Guid)); // reachable by GUID
        Assert.Null(db.ResolveById(0));               // id 0 is never indexed
    }

    [Fact]
    public void AddIfAbsent_KeepsWhatWasRegisteredFirst()
    {
        // Sources are merged with the game's own content first, so a workshop item reusing an official
        // legacy id (they were never unique across mods) must not take that id over: a plain placement
        // resolving by id would land on an unrelated mod object.
        var db = new ObjectAssetDatabase();
        ObjectAsset core = Make("2e698a7b85e94c019b3f91ec8796a961", 57, "Small");
        ObjectAsset mod = Make("0517b7a03b844929856fc4f72701fca9", 57, "Medium");

        db.AddIfAbsent(core);
        db.AddIfAbsent(mod);

        Assert.Same(core, db.ResolveById(57));
        Assert.Same(core, db.ResolveByGuid(core.Guid));

        // The mod's own GUID is still indexed: only the contested id went to the first claimant.
        Assert.Same(mod, db.ResolveByGuid(mod.Guid));
        Assert.Equal(2, db.Count);
    }

    [Fact]
    public void AddIfAbsent_SameGuidTwice_KeepsTheFirst()
    {
        var db = new ObjectAssetDatabase();
        ObjectAsset first = Make("2e698a7b85e94c019b3f91ec8796a961", 57, "Small");
        ObjectAsset duplicate = Make("2e698a7b85e94c019b3f91ec8796a961", 88, "Large");

        db.AddIfAbsent(first);
        db.AddIfAbsent(duplicate);

        Assert.Same(first, db.ResolveByGuid(first.Guid));
        Assert.Same(first, db.ResolveById(57));
        Assert.Same(duplicate, db.ResolveById(88)); // its own id was free
    }

    [Fact]
    public void AddIfAbsent_AssetWithoutId_IsIndexedByGuidOnly()
    {
        var db = new ObjectAssetDatabase();
        ObjectAsset asset = Make("2e698a7b85e94c019b3f91ec8796a961", 0, "Small");
        db.AddIfAbsent(asset);

        Assert.Same(asset, db.ResolveByGuid(asset.Guid));
        Assert.Null(db.ResolveById(0));
    }

    [Fact]
    public void Add_StillOverwrites_WithinASingleScan()
    {
        // Inside one bundle the last file read wins, which is what ScanDirectory documents; only the
        // cross-source merge uses AddIfAbsent.
        var db = new ObjectAssetDatabase();
        ObjectAsset first = Make("2e698a7b85e94c019b3f91ec8796a961", 57, "Small");
        ObjectAsset second = Make("0517b7a03b844929856fc4f72701fca9", 57, "Medium");

        db.Add(first);
        db.Add(second);

        Assert.Same(second, db.ResolveById(57));
    }

    [Fact]
    public void ResolveByGuid_Miss_IsNull()
    {
        Assert.Null(new ObjectAssetDatabase().ResolveByGuid(Guid.NewGuid()));
    }

    [Fact]
    public void ReadLocalizedName_NullDirectory_And_MissingFile()
    {
        Assert.Null(ObjectAssetDatabase.ReadLocalizedName(null));
        using var dir = new TempDir();
        Assert.Null(ObjectAssetDatabase.ReadLocalizedName(dir.Path)); // no English.dat
    }
}
