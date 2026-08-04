using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

// The layer table is the game's, read out of its TagManager: index 25 is "Ladder". Everything a collider
// means in the shipped content comes down to this number, so it is worth stating once and pinning.
public class UnityLayersTests
{
    [Fact]
    public void LadderIsLayer25() => Assert.Equal(25, UnityLayers.Ladder);

    [Theory]
    [InlineData(25, true)]
    [InlineData(0, false)]   // Default
    [InlineData(16, false)]  // Medium — where the ladder object itself sits
    [InlineData(20, false)]  // Ground
    [InlineData(26, false)]  // Vehicle
    public void OnlyLayer25IsALadder(int layer, bool expected)
        => Assert.Equal(expected, UnityLayers.IsLadder(layer));
}
