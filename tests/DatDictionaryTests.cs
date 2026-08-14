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

    // DatValueEx.TryParseGuid is Guid.TryParse and nothing more, so an all-zero GUID is well formed and
    // parses: the game's ParseGuid hands back Guid.Empty successfully. Only a malformed string fails.
    [Theory]
    [InlineData("00000000000000000000000000000000", true)] // well formed, and it is Guid.Empty
    [InlineData("not-a-guid", false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("2e698a7b85e94c019b3f91ec8796a961", true)]
    public void TryGetGuid(string value, bool expected)
    {
        DatDictionary d = DatParser.Parse($"G {value}\n");
        Assert.Equal(expected, d.TryGetGuid("G", out Guid guid));
    }

    [Fact]
    public void TryGetGuid_AllZero_ParsesAsEmpty()
    {
        Assert.True(DatParser.Parse("G 00000000000000000000000000000000\n").TryGetGuid("G", out Guid g));
        Assert.Equal(Guid.Empty, g);
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

    // Every numeric accessor in DatValueEx parses with NumberStyles.Any, which is far wider than
    // Integer/Float: it also allows a decimal point, thousands separators, parentheses for negation and
    // a currency symbol. The width is load-bearing, because a value that fails to parse silently leaves
    // the caller holding its default — `Health 200.0` is 200 to the game and would have been 0 here.
    [Theory]
    [InlineData("200.0", 200)]
    [InlineData("200.000000", 200)]
    [InlineData("1,000", 1000)]
    [InlineData("(5)", -5)]
    [InlineData(" 42 ", 42)]
    [InlineData("+7", 7)]
    public void TryGetInt32_UsesNumberStylesAny(string raw, int expected)
    {
        Assert.True(DatParser.Parse($"N {raw}\n").TryGetInt32("N", out int value));
        Assert.Equal(expected, value);
    }

    // Any does not make everything parse: a fractional part that is not zero still overflows an integer
    // parse, so the caller's default stands, exactly as it does in the game.
    [Theory]
    [InlineData("200.5")]
    [InlineData("abc")]
    [InlineData("")]
    public void TryGetInt32_StillRejectsWhatTheGameRejects(string raw)
    {
        Assert.False(DatParser.Parse($"N {raw}\n").TryGetInt32("N", out _));
    }

    [Fact]
    public void TryGetUInt16_AndByte_UseNumberStylesAny()
    {
        DatDictionary d = DatParser.Parse("H 200.0\nB 7.0\n");
        Assert.True(d.TryGetUInt16("H", out ushort h));
        Assert.Equal(200, h);
        Assert.True(d.TryGetByte("B", out byte b));
        Assert.Equal(7, b);
    }

    // DatValueEx.TryParseBool: a ONE-character value is read as a letter, and a longer one goes through
    // bool.TryParse. Anything it cannot read leaves the caller's default in place.
    [Theory]
    [InlineData("y", true)]
    [InlineData("t", true)]
    [InlineData("1", true)]
    [InlineData("n", false)]
    [InlineData("f", false)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    public void GetBool_ReadsTheGamesSpellings(string raw, bool expected)
    {
        DatDictionary d = DatParser.Parse($"Flag {raw}\n");
        Assert.Equal(expected, d.GetBool("Flag"));
        // The value parses, so the caller's default never comes into it either way.
        Assert.Equal(expected, d.GetBool("Flag", true));
        Assert.Equal(expected, d.GetBool("Flag", false));
    }

    // The half that was inverted here: a value that does not parse — including the null a bare flag
    // holds — returns FALSE from TryParseBool, so ParseBool yields the CALLER'S DEFAULT. It does not
    // mean true. This is why the game reads bare flags with ContainsKey and never with ParseBool
    // (ObjectAsset.cs:1154, 1165-1167; ResourceAsset.cs:483, 506).
    [Theory]
    [InlineData("Flag\n")]          // bare flag: DatValue(null)
    [InlineData("Flag yes\n")]      // a word bool.TryParse does not know
    [InlineData("Flag 2\n")]        // a single character that is not one of the six
    [InlineData("Flag x\n")]
    [InlineData("Flag \"\"\n")]     // present and empty
    public void GetBool_Unparseable_FallsBackToTheCallersDefault(string text)
    {
        DatDictionary d = DatParser.Parse(text);
        Assert.True(d.ContainsKey("Flag"));
        Assert.False(d.GetBool("Flag"));
        Assert.True(d.GetBool("Flag", defaultValue: true));
        Assert.False(d.TryGetBool("Flag", out _));
    }

    [Fact]
    public void GetBool_AbsentKey_IsTheDefault()
    {
        DatDictionary d = DatParser.Parse("Other 1\n");
        Assert.False(d.GetBool("absent"));
        Assert.True(d.GetBool("absent", defaultValue: true));
    }
}
