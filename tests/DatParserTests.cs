using UnturnedGodot.Dat;
using Xunit;

namespace UnturnedGodot.Tests;

public class DatParserTests
{
    [Fact]
    public void FlatKeyValues_WithBomAndComments()
    {
        string text = "﻿// header comment\nGUID 2e698a7b85e94c019b3f91ec8796a961\nType Small\nID 57\nName Cardboard #1\n";
        DatDictionary d = DatParser.Parse(text);

        Assert.Equal("Small", d.GetString("Type"));
        Assert.Equal("57", d.GetString("ID"));
        Assert.Equal("Cardboard #1", d.GetString("Name")); // value keeps internal spaces
        Assert.True(d.TryGetGuid("GUID", out _));
    }

    [Fact]
    public void KeysAreCaseInsensitive()
    {
        DatDictionary d = DatParser.Parse("Type Small\n");
        Assert.Equal("Small", d.GetString("TYPE"));
    }

    [Fact]
    public void CrlfLineEndings()
    {
        DatDictionary d = DatParser.Parse("A 1\r\nB 2\r\n");
        Assert.Equal("1", d.GetString("A"));
        Assert.Equal("2", d.GetString("B"));
    }

    [Fact]
    public void KeyWithoutValue_BecomesEmpty()
    {
        DatDictionary d = DatParser.Parse("Flag\nNext 1\n");
        Assert.Equal("", d.GetString("Flag"));
        Assert.Equal("1", d.GetString("Next"));
    }

    [Fact]
    public void QuotedKeyAndValue_WithEscapes()
    {
        DatDictionary d = DatParser.Parse("\"my key\" \"line1\\nline2\\ttab\\\\end\\\"q\"\n");
        Assert.Equal("line1\nline2\ttab\\end\"q", d.GetString("my key"));
    }

    [Fact]
    public void UnquotedValueEscapes_AndUnknownEscape()
    {
        DatDictionary d = DatParser.Parse("K a\\tb\\z\n"); // \t -> tab, \z -> z
        Assert.Equal("a\tbz", d.GetString("K"));
    }

    [Fact]
    public void NestedDictionary()
    {
        DatDictionary d = DatParser.Parse("Metadata\n{\nGUID abc\nType SDG.Unturned.ObjectAsset\n}\nID 5\n");
        Assert.True(d.TryGetDictionary("Metadata", out var md));
        Assert.Equal("abc", md.GetString("GUID"));
        Assert.Equal("5", d.GetString("ID"));
    }

    [Fact]
    public void NextLineBraceOverridesInlineValue()
    {
        // Key has an inline value, but a '{' on the next line wins (Unturned semantics).
        DatDictionary d = DatParser.Parse("Asset ignored\n{\nType Large\n}\n");
        Assert.True(d.TryGetDictionary("Asset", out var a));
        Assert.Equal("Large", a.GetString("Type"));
    }

    [Fact]
    public void ScalarList()
    {
        DatDictionary d = DatParser.Parse("Items\n[\nalpha\nbeta\ngamma\n]\n");
        Assert.True(d.TryGetList("Items", out var list));
        Assert.Equal(3, list.Items.Count);
        Assert.Equal("beta", ((DatValue)list.Items[1]).Value);
    }

    [Fact]
    public void ListOfDictionariesAndLists_WithCommas()
    {
        // Braces/brackets on their own lines (Unturned format); trailing commas exercise ConsumeBracket.
        DatDictionary d = DatParser.Parse("Tiles\n[\n{,\nX 1\n},\n[,\na\nb\n],\n]\n");
        Assert.True(d.TryGetList("Tiles", out var list));
        Assert.Equal(2, list.Items.Count);
        Assert.IsType<DatDictionary>(list.Items[0]);
        Assert.IsType<DatList>(list.Items[1]);
    }

    [Fact]
    public void ToleratesStrayCloseBracketsAtRoot()
    {
        // Stray ']' and '}' at the root must not crash and must not lose prior keys.
        DatDictionary d = DatParser.Parse("A 1\nB 2\n]\n}\n");
        Assert.Equal("1", d.GetString("A"));
        Assert.Equal("2", d.GetString("B"));
    }

    [Fact]
    public void LeadingWhitespaceAndStandaloneCommas_AreSkipped()
    {
        // Leading spaces (IsWhiteSpace) and a comma outside any bracket both skip at the loop top.
        DatDictionary d = DatParser.Parse("  A 1\n,B 2\n");
        Assert.Equal("1", d.GetString("A"));
        Assert.Equal("2", d.GetString("B"));
    }

    [Fact]
    public void TrailingBackslash_InUnquotedValue_IsLiteral()
    {
        // Backslash with no following char (end of input) is kept literally.
        DatDictionary d = DatParser.Parse("K abc\\");
        Assert.Equal("abc\\", d.GetString("K"));
    }

    [Fact]
    public void UnclosedQuotedValue_ReadsToEnd()
    {
        DatDictionary d = DatParser.Parse("K \"abc");
        Assert.Equal("abc", d.GetString("K"));
    }

    [Fact]
    public void QuotedValue_TrailingComma_IsConsumed()
    {
        DatDictionary d = DatParser.Parse("K \"v\",\nNext 1\n");
        Assert.Equal("v", d.GetString("K"));
        Assert.Equal("1", d.GetString("Next"));
    }

    [Fact]
    public void EmptyInput_ReturnsEmptyDictionary()
    {
        Assert.Null(DatParser.Parse("").GetString("anything"));
    }

    [Fact]
    public void KeyAtEndOfInput_NoValue()
    {
        // "Solo" with no trailing whitespace: key runs to EOF and has no value.
        Assert.Equal("", DatParser.Parse("Solo").GetString("Solo"));
    }

    [Fact]
    public void KeyFollowedByCarriageReturn_HasNoValue()
    {
        DatDictionary d = DatParser.Parse("A\r\nB 2\r\n");
        Assert.Equal("", d.GetString("A"));
        Assert.Equal("2", d.GetString("B"));
    }

    [Fact]
    public void TabSeparatesKeyFromValue()
    {
        Assert.Equal("V", DatParser.Parse("K\tV\n").GetString("K"));
    }

    [Fact]
    public void CloseBracketAtEndOfInput()
    {
        // '}' as the final character exercises ConsumeBracket's end-of-input guard.
        DatDictionary d = DatParser.Parse("A 1\n}");
        Assert.Equal("1", d.GetString("A"));
    }

    [Fact]
    public void MismatchedCloseInsideList_IsTolerated()
    {
        // '}' inside a '[' pops back to dict context; the list parser skips the stray token.
        DatDictionary d = DatParser.Parse("K\n[\n}\n");
        Assert.True(d.TryGetList("K", out _));
    }
}
