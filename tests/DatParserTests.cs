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

    // ReadDictionaryValue builds `new DatValue(null)` when the token after the key was not a Value
    // (SDK DatParser.cs:267), so a bare flag is a key holding null rather than an empty string. That is
    // why the game reads flags like this with ContainsKey and never by parsing them.
    [Fact]
    public void KeyWithoutValue_IsNull()
    {
        DatDictionary d = DatParser.Parse("Flag\nNext 1\n");
        Assert.True(d.ContainsKey("Flag"));
        Assert.True(d.TryGetString("Flag", out string? flag));
        Assert.Null(flag);
        Assert.Null(d.GetString("Flag"));
        Assert.Equal("1", d.GetString("Next"));
    }

    [Fact]
    public void QuotedKeyAndValue_WithEscapes()
    {
        DatDictionary d = DatParser.Parse("\"my key\" \"line1\\nline2\\ttab\\\\end\\\"q\"\n");
        Assert.Equal("line1\nline2\ttab\\end\"q", d.GetString("my key"));
    }

    // \t is a tab, but \z is NOT z: DatTokenizer.ReadStringValue re-appends the backslash it had
    // skipped for any escape other than n, t or \\ (SDK DatTokenizer.cs:456-464). 3.23.7.0 added \n
    // handling to unquoted strings and broke the mods writing Windows paths, and this is the workaround
    // that shipped for them.
    [Fact]
    public void UnquotedValueEscapes_UnknownEscapeKeepsItsBackslash()
    {
        DatDictionary d = DatParser.Parse("K a\\tb\\z\n");
        Assert.Equal("a\tb\\z", d.GetString("K"));
    }

    // The case the workaround exists for: a Windows path survives with its separators intact.
    [Fact]
    public void UnquotedValue_WindowsPath_KeepsItsSeparators()
    {
        Assert.Equal(@"Some\Path\To\File.png",
            DatParser.Parse(@"Icon Some\Path\To\File.png" + "\n").GetString("Icon"));
    }

    // A quoted run recognizes \" as well, because a bare quote would end it; an unquoted one does not,
    // so there the backslash stays (SDK DatTokenizer.cs:365-373 against :456-464).
    [Fact]
    public void EscapedQuote_IsRecognizedOnlyInsideQuotes()
    {
        Assert.Equal("a\"b", DatParser.Parse("K \"a\\\"b\"\n").GetString("K"));
        Assert.Equal("a\\\"b", DatParser.Parse("K a\\\"b\n").GetString("K"));
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

    // ReadDictionaryValue advances past AT MOST ONE line break before it looks for a bracket (SDK
    // DatParser.cs:229-237), so a blank line in between means the '{' is not this key's value: the key
    // keeps its scalar and the block is parsed as though nothing had introduced it.
    [Fact]
    public void BlankLineBeforeBrace_LeavesTheKeyScalar()
    {
        DatDictionary d = DatParser.Parse("Asset kept\n\n{\nType Large\n}\n");
        Assert.False(d.TryGetDictionary("Asset", out _));
        Assert.Equal("kept", d.GetString("Asset"));
    }

    // The same for a list, and for a key that had no inline value at all: it stays a null scalar.
    [Fact]
    public void BlankLineBeforeBracket_LeavesTheKeyScalar()
    {
        DatDictionary d = DatParser.Parse("Items\n\n[\na\n]\n");
        Assert.False(d.TryGetList("Items", out _));
        Assert.True(d.ContainsKey("Items"));
        Assert.Null(d.GetString("Items"));
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

    // DatParser.Parse's root loop switches on Key and Comment only, so a CloseDictionary falls through
    // to `default:` and merely advances (SDK DatParser.cs:41-65). The root body has no closer to find
    // and therefore cannot end early: one unbalanced '}' costs a token, not the rest of the file.
    [Fact]
    public void StrayCloseBraceAtRoot_DoesNotTruncateTheDocument()
    {
        DatDictionary d = DatParser.Parse("Name Before\n}\nDescription After\nID 7\n");
        Assert.Equal("Before", d.GetString("Name"));
        Assert.Equal("After", d.GetString("Description"));
        Assert.Equal("7", d.GetString("ID"));
    }

    // The same, one level down: the '}' that closes a nested block leaves the root body running.
    [Fact]
    public void ExtraCloseBraceAfterNestedBlock_KeepsReadingTheRoot()
    {
        DatDictionary d = DatParser.Parse("Sub\n{\nX 1\n}\n}\nAfter 2\n");
        Assert.True(d.TryGetDictionary("Sub", out _));
        Assert.Equal("2", d.GetString("After"));
    }

    [Fact]
    public void LeadingWhitespace_IsSkipped()
    {
        Assert.Equal("1", DatParser.Parse("  A 1\n").GetString("A"));
    }

    // DatTokenizer's main loop has no case for a comma: it eats one only where it is tight against a
    // bracket or a closing quote (SDK DatTokenizer.cs:176-211, :404-407). A comma anywhere else is an
    // ordinary character, and ReadDictionaryKey runs to the next whitespace — so ",B" is the key.
    [Fact]
    public void StandaloneComma_IsPartOfTheKey_NotWhitespace()
    {
        DatDictionary d = DatParser.Parse(",B 2\n");
        Assert.Equal("2", d.GetString(",B"));
        Assert.False(d.ContainsKey("B"));
    }

    // The commas the tokenizer really does swallow: one tight against a bracket, one after a quote.
    [Fact]
    public void CommasTightAgainstBracketsAndQuotes_AreConsumed()
    {
        DatDictionary d = DatParser.Parse("Items\n[,\n\"a\",\n\"b\",\n],\nAfter 1\n");
        Assert.True(d.TryGetList("Items", out var list));
        Assert.Equal(2, list.Items.Count);
        Assert.Equal("1", d.GetString("After"));
    }

    // A backslash with nothing after it is DROPPED, not kept: the game sets escapeNextChar, reads past
    // the end of input, and its do/while exits on !hasChar before anything is appended
    // (SDK DatTokenizer.cs:472-484).
    [Fact]
    public void TrailingBackslash_AtEndOfInput_IsDropped()
    {
        Assert.Equal("abc", DatParser.Parse("K abc\\").GetString("K"));
        Assert.Equal("abc", DatParser.Parse("K \"abc\\").GetString("K"));
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
        // "Solo" with no trailing whitespace: key runs to EOF and has no value, which is a null.
        DatDictionary d = DatParser.Parse("Solo");
        Assert.True(d.ContainsKey("Solo"));
        Assert.Null(d.GetString("Solo"));
    }

    [Fact]
    public void KeyFollowedByCarriageReturn_HasNoValue()
    {
        DatDictionary d = DatParser.Parse("A\r\nB 2\r\n");
        Assert.True(d.ContainsKey("A"));
        Assert.Null(d.GetString("A"));
        Assert.Equal("2", d.GetString("B"));
    }

    // SkipSpacesAndTabs only eats spaces and tabs, and the gate after it is !char.IsWhiteSpace (SDK
    // DatTokenizer.cs:222-226). Any other whitespace between key and value therefore starts no value:
    // the key gets none and the next word becomes a key of its own.
    [Fact]
    public void OtherWhitespaceBetweenKeyAndValue_StartsNoValue()
    {
        DatDictionary d = DatParser.Parse("A\u000bB 2\n");
        Assert.True(d.ContainsKey("A"));
        Assert.Null(d.GetString("A"));
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
        // '}' inside a '[' does not match the top of the context stack, so the context stays List and
        // the list parser skips the stray token.
        DatDictionary d = DatParser.Parse("K\n[\n}\n");
        Assert.True(d.TryGetList("K", out _));
    }

    // DatTokenizer.PopContext unwinds only when the closer MATCHES the top of the stack (SDK
    // DatTokenizer.cs:516-539). The '}' below finds a List there and leaves it, so the words after it
    // are still list values; popping unconditionally would have made them a key and a value instead.
    [Fact]
    public void MismatchedCloseInsideList_KeepsTheListContext()
    {
        DatDictionary d = DatParser.Parse("K\n[\n}\nfoo bar\n]\n");
        Assert.True(d.TryGetList("K", out var list));
        Assert.Single(list.Items);
        Assert.Equal("foo bar", ((DatValue)list.Items[0]).Value);
    }

    // A '{' inside a list is a real nested dictionary, so a ']' inside THAT does not close the list:
    // it finds a Dictionary on top of the stack and is discarded.
    [Fact]
    public void MismatchedCloseInsideNestedDictionary_KeepsTheDictionaryContext()
    {
        DatDictionary d = DatParser.Parse("K\n[\n{\n]\nName Inner\n}\n]\n");
        Assert.True(d.TryGetList("K", out var list));
        var inner = Assert.IsType<DatDictionary>(list.Items[0]);
        Assert.Equal("Inner", inner.GetString("Name"));
    }
}
