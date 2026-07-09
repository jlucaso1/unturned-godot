using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using Xunit;

namespace UnturnedGodot.Tests;

public class ObjectColorTests
{
    private static ObjectAsset Asset(string type)
    {
        DatDictionary root = DatParser.Parse($"GUID 2e698a7b85e94c019b3f91ec8796a961\nType {type}\nID 1\n");
        ObjectAsset.TryParse(root, null, out ObjectAsset a);
        return a;
    }

    [Theory]
    [InlineData("Small")]
    [InlineData("Medium")]
    [InlineData("Large")]
    [InlineData("NPC")]
    [InlineData("Decal")]
    [InlineData("Resource")]
    [InlineData("Structure")] // -> Unknown
    public void DistinctColorPerType(string type)
    {
        Assert.NotEqual(ObjectColor.Unresolved, ObjectColor.ForAsset(Asset(type)));
    }

    [Fact]
    public void NullAsset_IsUnresolvedColor()
    {
        Assert.Equal(ObjectColor.Unresolved, ObjectColor.ForAsset(null));
    }
}
