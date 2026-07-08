using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class CommonStringTests
{
    [Fact]
    public void KnownOffset_ReturnsString()
    {
        Assert.Equal("AABB", CommonString.Get(0));
        Assert.Equal("GUID", CommonString.Get(208));
    }

    [Fact]
    public void UnknownOffset_ReturnsOffsetText()
    {
        Assert.Equal("999999", CommonString.Get(999999));
    }
}
