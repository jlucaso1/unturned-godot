using System;
using UnturnedGodot.Dat;
using Xunit;

namespace UnturnedGodot.Tests;

public class DatDictionaryTests
{
    [Fact]
    public void TryGetString_MissingAndWrongType()
    {
        DatDictionary d = DatParser.Parse("Sub\n{\nX 1\n}\n");
        Assert.False(d.TryGetString("absent", out _));
        Assert.False(d.TryGetString("Sub", out _)); // a dictionary, not a value
        Assert.Null(d.GetString("absent"));
    }

    [Fact]
    public void TryGetDictionary_And_List_Misses()
    {
        DatDictionary d = DatParser.Parse("Val 1\n");
        Assert.False(d.TryGetDictionary("Val", out _));
        Assert.False(d.TryGetList("Val", out _));
        Assert.False(d.TryGetDictionary("absent", out _));
        Assert.False(d.TryGetList("absent", out _));
    }

    [Fact]
    public void TryGetList_Hit()
    {
        DatDictionary d = DatParser.Parse("Items\n[\na\nb\n]\n");
        Assert.True(d.TryGetList("Items", out var list));
        Assert.Equal(2, list.Items.Count);
    }

    [Theory]
    [InlineData("00000000000000000000000000000000", false)] // empty guid rejected
    [InlineData("not-a-guid", false)]
    [InlineData("2e698a7b85e94c019b3f91ec8796a961", true)]
    public void TryGetGuid(string value, bool expected)
    {
        DatDictionary d = DatParser.Parse($"G {value}\n");
        Assert.Equal(expected, d.TryGetGuid("G", out Guid guid));
        if (expected) Assert.NotEqual(Guid.Empty, guid);
    }

    [Fact]
    public void TryGetGuid_AbsentKey_IsFalse()
    {
        Assert.False(DatParser.Parse("X 1\n").TryGetGuid("absent", out _));
    }

    [Fact]
    public void TryGetUInt16_ValidAndInvalid()
    {
        DatDictionary d = DatParser.Parse("Good 57\nBad xx\n");
        Assert.True(d.TryGetUInt16("Good", out ushort v));
        Assert.Equal(57, v);
        Assert.False(d.TryGetUInt16("Bad", out _));
    }

    [Fact]
    public void DuplicateKey_LastWins()
    {
        DatDictionary d = DatParser.Parse("K a\nK b\n");
        Assert.Equal("b", d.GetString("K"));
    }
}
