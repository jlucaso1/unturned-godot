using System;
using Xunit;

namespace UnturnedGodot.Tests.Assets;

// The name every object collision body carries, and the way back out of it.
//
// The writer and the reader are one file precisely so they cannot drift, and this is what proves they
// have not: a round trip. The failure it guards against is invisible in play — a punch would simply stop
// damaging anything and read as a targeting bug — so nothing else would catch it.
public class ObjectCollisionNamesTests
{
    [Fact]
    public void APlainNameRoundTrips()
    {
        var guid = Guid.NewGuid();

        Assert.True(ObjectCollisionNames.TryParseGuid(ObjectCollisionNames.For(guid), out Guid parsed));
        Assert.Equal(guid, parsed);
    }

    // A body that covers one cell of a streamed asset carries the cell in its name too, and the GUID has
    // to survive that: the cell is how several bodies of one asset are told apart, not part of its identity.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, -7)]
    [InlineData(-12, 40)]
    public void ACelledNameRoundTripsToTheSameGuid(int cellX, int cellZ)
    {
        var guid = Guid.NewGuid();

        Assert.True(ObjectCollisionNames.TryParseGuid(
            ObjectCollisionNames.For(guid, cellX, cellZ), out Guid parsed));
        Assert.Equal(guid, parsed);
    }

    [Fact]
    public void TheCellIsPartOfTheNameEvenThoughItIsNotPartOfTheIdentity()
    {
        var guid = Guid.NewGuid();

        Assert.NotEqual(ObjectCollisionNames.For(guid), ObjectCollisionNames.For(guid, 1, 2));
        Assert.NotEqual(ObjectCollisionNames.For(guid, 1, 2), ObjectCollisionNames.For(guid, 2, 1));
    }

    // Everything a physics query can hit that is NOT one of ours: terrain, a ladder volume, a body some
    // other system named. Each has to come back false rather than parse into a plausible-looking GUID.
    [Theory]
    [InlineData("Terrain")]
    [InlineData("Ladder_3")]
    [InlineData("Col_")]
    [InlineData("Col_notaguid")]
    [InlineData("Col_notaguid_1_2")]
    [InlineData("")]
    [InlineData("_Col_00000000000000000000000000000000")]
    public void AnythingThatIsNotOneOfOursIsRejected(string name)
    {
        Assert.False(ObjectCollisionNames.TryParseGuid(name, out Guid guid), $"'{name}' parsed as an asset");
        Assert.Equal(Guid.Empty, guid);
    }

    // A null name reaches this from a body the query found but nothing named. It is a rejection, not a throw.
    [Fact]
    public void ANullNameIsRejectedRatherThanThrowing()
    {
        Assert.False(ObjectCollisionNames.TryParseGuid(null!, out Guid guid));
        Assert.Equal(Guid.Empty, guid);
    }

    // The "N" format: 32 hex digits, no braces and no dashes. Pinned because the cell suffix is split on
    // '_' and a dashed GUID would still parse — leaving two spellings of the same body in circulation.
    [Fact]
    public void TheNameUsesTheDashlessGuidForm()
    {
        var guid = new Guid("01234567-89ab-cdef-0123-456789abcdef");

        Assert.Equal("Col_0123456789abcdef0123456789abcdef", ObjectCollisionNames.For(guid));
        Assert.Equal("Col_0123456789abcdef0123456789abcdef_2_-3",
            ObjectCollisionNames.For(guid, 2, -3));
    }
}
