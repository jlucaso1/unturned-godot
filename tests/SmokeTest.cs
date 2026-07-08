using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

public class SmokeTest
{
    [Fact]
    public void GodotStructsWorkOutsideEngine()
    {
        var v = new Vector3(1, 2, 3);
        Assert.Equal(new Vector3(1, 2, -3), Landscape.UnityToGodot(v));
    }
}
