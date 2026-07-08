using UnturnedGodot.Dat;
using Xunit;

namespace UnturnedGodot.Tests;

public class LegacyDataTests
{
    [Fact]
    public void SplitsOnFirstSpace_KeepsRemainder()
    {
        LegacyData d = LegacyData.Parse("Name Cardboard #1\r\nDescription A box of stuff\n");
        Assert.Equal("Cardboard #1", d.GetString("Name"));
        Assert.Equal("A box of stuff", d.GetString("Description"));
    }

    [Fact]
    public void KeyWithoutValue_And_CommentsAndBlankLines()
    {
        LegacyData d = LegacyData.Parse("// comment\n\nFlag\nName Box\n");
        Assert.Equal("", d.GetString("Flag"));
        Assert.Equal("Box", d.GetString("Name"));
    }

    [Fact]
    public void MissingKey_ReturnsNull()
    {
        Assert.Null(LegacyData.Parse("Name Box\n").GetString("Absent"));
    }
}
