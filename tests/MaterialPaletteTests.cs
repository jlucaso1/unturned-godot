using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using Xunit;

namespace UnturnedGodot.Tests;

public class MaterialPaletteTests
{
    [Fact]
    public void ParsesMetadataGuidAndMaterialPaths()
    {
        string text =
            "Metadata\n{\nGUID 3fcc42609bfb4154abb9dd39e7542ed8\n}\n" +
            "Asset\n{\nID 0\nMaterials\n[\n{\nName core.masterbundle\nPath Objects/Palettes/A.mat\n}\n" +
            "{\nPath Objects/Palettes/B.mat\n}\n]\n}\n";
        MaterialPalette? palette = MaterialPalette.Read(DatParser.Parse(text));

        Assert.NotNull(palette);
        Assert.NotEqual(System.Guid.Empty, palette!.Guid);
        Assert.Equal(new[] { "Objects/Palettes/A.mat", "Objects/Palettes/B.mat" }, palette.MaterialPaths);
    }

    [Fact]
    public void MissingGuid_ReturnsNull()
    {
        Assert.Null(MaterialPalette.Read(DatParser.Parse("Asset\n{\nMaterials\n[\n]\n}\n")));
    }

    [Fact]
    public void NoMaterials_ReturnsEmptyList()
    {
        MaterialPalette? palette = MaterialPalette.Read(
            DatParser.Parse("GUID 3fcc42609bfb4154abb9dd39e7542ed8\n"));
        Assert.NotNull(palette);
        Assert.Empty(palette!.MaterialPaths);
    }

    [Fact]
    public void EntryWithoutPath_IsSkipped()
    {
        string text = "GUID 3fcc42609bfb4154abb9dd39e7542ed8\nMaterials\n[\n{\nName only\n}\n{\nPath Real.mat\n}\n]\n";
        MaterialPalette? palette = MaterialPalette.Read(DatParser.Parse(text));
        Assert.Equal(new[] { "Real.mat" }, palette!.MaterialPaths);
    }

    [Fact]
    public void ScalarListEntry_IsSkipped()
    {
        // A non-dictionary item in the Materials list is ignored.
        string text = "GUID 3fcc42609bfb4154abb9dd39e7542ed8\nMaterials\n[\nscalar\n{\nPath Real.mat\n}\n]\n";
        MaterialPalette? palette = MaterialPalette.Read(DatParser.Parse(text));
        Assert.Equal(new[] { "Real.mat" }, palette!.MaterialPaths);
    }
}
