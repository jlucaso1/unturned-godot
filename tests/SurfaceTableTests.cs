using Xunit;

namespace UnturnedGodot.Tests;

// The byte a collision placement carries instead of a physics-material name, and what it reads back as.
public class SurfaceTableTests
{
    [Fact]
    public void TheEmptyNameIsIndexZeroAndTakesNoTableEntry()
    {
        var table = new SurfaceTable();

        Assert.Equal(0, table.IndexOf(string.Empty));
        Assert.Empty(table.Names);
    }

    [Fact]
    public void InternsEachNameOnceAndNumbersThemFromOne()
    {
        var table = new SurfaceTable();

        byte wood = table.IndexOf("Wood_Static");
        byte metal = table.IndexOf("Metal_Static");

        Assert.Equal(1, wood);
        Assert.Equal(2, metal);
        Assert.Equal(wood, table.IndexOf("Wood_Static"));
        Assert.Equal(new[] { "Wood_Static", "Metal_Static" }, table.Names);
    }

    // Names are Unity object names and are matched by the game case-sensitively; two that differ only in
    // case are two materials, not one.
    [Fact]
    public void NamesAreDistinguishedByCase()
    {
        var table = new SurfaceTable();

        Assert.NotEqual(table.IndexOf("Wood_Static"), table.IndexOf("wood_static"));
    }

    [Fact]
    public void NameAt_MapsAnIndexBackToItsName()
    {
        var table = new SurfaceTable();
        table.IndexOf("Wood_Static");
        table.IndexOf("Metal_Static");

        Assert.Equal("Wood_Static", SurfaceTable.NameAt(table.Names, 1));
        Assert.Equal("Metal_Static", SurfaceTable.NameAt(table.Names, 2));
    }

    // Zero is "no material" and anything past the table is corruption; both read as no material, which is
    // a surface that makes no sound and leaves no mark — exactly what a collider with no PhysicMaterial
    // does in the original.
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(255)]
    public void NameAt_ZeroOrPastTheTable_IsEmpty(byte index)
    {
        var table = new SurfaceTable();
        table.IndexOf("Wood_Static");
        table.IndexOf("Metal_Static");

        Assert.Equal(string.Empty, SurfaceTable.NameAt(table.Names, index));
    }

    // A byte cannot address more than 255 surfaces. Far beyond anything the content ships — the whole game
    // defines 21 — but the overflow has to degrade to "no material" rather than wrap onto another surface,
    // which would give a wall the sound of a corpse.
    [Fact]
    public void PastTwoHundredAndFiftyFiveNames_TheOverflowIsNoMaterial()
    {
        var table = new SurfaceTable();
        for (int i = 0; i < 255; i++)
            Assert.Equal(i + 1, table.IndexOf($"Surface_{i}"));

        Assert.Equal(0, table.IndexOf("Surface_255"));
        Assert.Equal(255, table.Names.Count);
    }
}
