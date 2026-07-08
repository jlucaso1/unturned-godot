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
        Assert.True(ObjectAsset.TryParse(root, null, out ObjectAsset asset));
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
