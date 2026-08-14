using System.Collections.Generic;
using System.Text;

namespace UnturnedGodot.Dat;

// Ports Unturned's DatTokenizer + DatParser grammar (UnturnedDat/). Root is always a dictionary.
// Grammar highlights: comments start with '/' to end-of-line; line breaks are significant;
// a dictionary key is the first whitespace-delimited word (or a quoted string) and its value is
// the rest of the line, unless a '{'/'[' opens on the next line (which then wins over an inline value).
//
// The game's parser never stops on a malformed document: it records an error message and carries on,
// because — in its own words — "lots of third-party assets have typos which technically work correctly
// if ignored" (DatParser.cs:452-455). Nothing here collects those messages, but every rule they are
// attached to is reproduced, since what the game does *after* logging one is what decides how a
// workshop asset with a stray bracket or a Windows path in it actually reads.
public static class DatParser
{
    private enum TokType { Key, Value, OpenDict, CloseDict, OpenList, CloseList, LineBreak }

    // DatTokenizer.EContext. The stack starts EMPTY and GetContext falls back to Dictionary, so a
    // closer with nothing to close leaves the context alone instead of unwinding past the root.
    private enum Context { Dictionary, List }

    private readonly struct Tok
    {
        public readonly TokType Type;
        public readonly string Text;
        public Tok(TokType type, string text = "")
        {
            Type = type;
            Text = text;
        }
    }

    public static DatDictionary Parse(string text)
    {
        List<Tok> tokens = Tokenize(text);
        int i = 0;
        return ParseDictionaryBody(tokens, ref i, root: true);
    }

    // --- Tokenizer ---

    private static List<Tok> Tokenize(string text)
    {
        var tokens = new List<Tok>();
        // Context stack decides whether a bare word is a key (dictionary) or a value (list).
        var contextStack = new List<Context>();

        int p = 0;
        int n = text.Length;
        if (n >= 1 && text[0] == '﻿') p = 1; // skip UTF-8 BOM

        while (p < n)
        {
            char c = text[p];

            if (c == '/')
            {
                while (p < n && text[p] != '\n' && text[p] != '\r') p++;
            }
            else if (c == '\r' || c == '\n')
            {
                tokens.Add(new Tok(TokType.LineBreak));
                if (c == '\r' && p + 1 < n && text[p + 1] == '\n') p++;
                p++;
            }
            else if (c == '{')
            {
                tokens.Add(new Tok(TokType.OpenDict));
                contextStack.Add(Context.Dictionary);
                p = ConsumeBracket(text, p);
            }
            else if (c == '}')
            {
                PopContext(contextStack, Context.Dictionary);
                tokens.Add(new Tok(TokType.CloseDict));
                p = ConsumeBracket(text, p);
            }
            else if (c == '[')
            {
                tokens.Add(new Tok(TokType.OpenList));
                contextStack.Add(Context.List);
                p = ConsumeBracket(text, p);
            }
            else if (c == ']')
            {
                PopContext(contextStack, Context.List);
                tokens.Add(new Tok(TokType.CloseList));
                p = ConsumeBracket(text, p);
            }
            else if (char.IsWhiteSpace(c))
            {
                p++;
            }
            else if (GetContext(contextStack) == Context.List)
            {
                tokens.Add(new Tok(TokType.Value, ReadStringValue(text, ref p)));
            }
            else
            {
                tokens.Add(new Tok(TokType.Key, ReadKey(text, ref p)));
                SkipSpacesAndTabs(text, ref p);
                // DatTokenizer's gate is !char.IsWhiteSpace, not "not a line break". SkipSpacesAndTabs
                // only eats spaces and tabs, so any OTHER whitespace — a vertical tab, a form feed, a
                // non-breaking space — is still sitting there, and the game starts no value on it: the
                // key gets none and the next word becomes a key of its own.
                if (p < n && !char.IsWhiteSpace(text[p]))
                {
                    tokens.Add(new Tok(TokType.Value, ReadStringValue(text, ref p)));
                }
            }
        }
        return tokens;
    }

    // DatTokenizer.GetContext: an empty stack reads as Dictionary, which is what makes the root a
    // dictionary body without anything having opened it.
    private static Context GetContext(List<Context> stack) =>
        stack.Count > 0 ? stack[^1] : Context.Dictionary;

    // DatTokenizer.PopContext: a closer unwinds the stack ONLY when it matches the top of it. That is
    // the difference between `[ } foo bar ]` reading `foo bar` as a list value — which is what the game
    // does, because the '}' finds a List on top and leaves it there — and reading it as a key/value
    // pair, which is what popping unconditionally would produce. The game logs "unexpected end of
    // dictionary/object" and keeps the context; only the message is dropped here.
    private static void PopContext(List<Context> stack, Context expected)
    {
        int count = stack.Count;
        if (count > 0 && stack[count - 1] == expected) stack.RemoveAt(count - 1);
    }

    private static int ConsumeBracket(string text, int p)
    {
        p++;
        if (p < text.Length && text[p] == ',') p++;
        return p;
    }

    private static void SkipSpacesAndTabs(string text, ref int p)
    {
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) p++;
    }

    // DatTokenizer.ReadDictionaryKey. An unquoted key runs to the next whitespace with no escape
    // handling at all — brackets and commas included, so `Name{` is one key.
    private static string ReadKey(string text, ref int p)
    {
        if (text[p] == '"') return ReadQuoted(text, ref p);
        int start = p;
        while (p < text.Length && !char.IsWhiteSpace(text[p])) p++;
        return text.Substring(start, p - start);
    }

    private static string ReadStringValue(string text, ref int p)
    {
        if (text[p] == '"') return ReadQuoted(text, ref p);

        // Escapes are rare in asset data, so scan for the end first and slice when there are none. The
        // character-at-a-time StringBuilder is only worth its allocation and its regrowth when there is
        // actually something to unescape — and this runs for every value of every .dat in the install.
        int start = p;
        int scan = p;
        while (scan < text.Length && text[scan] != '\r' && text[scan] != '\n' && text[scan] != '\\') scan++;
        if (scan >= text.Length || text[scan] != '\\')
        {
            p = scan;
            return text.Substring(start, scan - start);
        }

        var sb = new StringBuilder(text.Length - start);
        sb.Append(text, start, scan - start);
        p = scan;
        while (p < text.Length)
        {
            char c = text[p];
            if (c == '\r' || c == '\n') break;
            if (c == '\\')
            {
                p++;
                // A backslash with nothing after it is DROPPED. The game sets escapeNextChar, reads past
                // the end of input and its do/while exits on !hasChar, so the backslash never reaches
                // the builder (DatTokenizer.cs:472-484).
                if (p >= text.Length) break;
                AppendEscape(sb, text[p], quoted: false);
                p++;
                continue;
            }
            sb.Append(c);
            p++;
        }
        return sb.ToString();
    }

    // DatTokenizer.ReadQuotedString: runs to the first unescaped quote, crossing line breaks to get
    // there, and takes one comma tight against the closing quote with it.
    private static string ReadQuoted(string text, ref int p)
    {
        p++; // opening quote

        // Same shape as ReadStringValue: slice when the quoted run has no escapes, which is the norm.
        int start = p;
        int scan = p;
        while (scan < text.Length && text[scan] != '"' && text[scan] != '\\') scan++;
        if (scan >= text.Length || text[scan] != '\\')
        {
            p = scan;
            if (p < text.Length) p++; // closing quote
            if (p < text.Length && text[p] == ',') p++;
            return text.Substring(start, scan - start);
        }

        var sb = new StringBuilder(text.Length - start);
        sb.Append(text, start, scan - start);
        p = scan;
        while (p < text.Length && text[p] != '"')
        {
            char c = text[p];
            if (c == '\\')
            {
                p++;
                if (p >= text.Length) break; // trailing backslash at end of input, dropped as above
                AppendEscape(sb, text[p], quoted: true);
                p++;
                continue;
            }
            sb.Append(c);
            p++;
        }
        if (p < text.Length) p++; // closing quote
        if (p < text.Length && text[p] == ',') p++;
        return sb.ToString();
    }

    // The escape table both readers share (DatTokenizer.cs:356-373 and :446-464). 'n' and 't' become the
    // control characters and a doubled backslash is one backslash — but ANY OTHER escape keeps the
    // backslash that introduced it, and the game logs "unrecognized escape sequence".
    //
    // That re-attachment is not incidental: 3.23.7.0 added '\n' handling to UNQUOTED strings, which
    // broke the mods that were writing Windows paths, and this is the workaround SDG shipped for them.
    // So `Some\Path` is `Some\Path`, not `SomePath`, and it is the divergence most likely to reach real
    // workshop content.
    //
    // A quoted run also recognizes \" ; an unquoted one does not, because a bare '"' does not end one —
    // so `\"` inside an unquoted value stays as the two characters it was written as.
    private static void AppendEscape(StringBuilder sb, char c, bool quoted)
    {
        switch (c)
        {
            case 'n': sb.Append('\n'); break;
            case 't': sb.Append('\t'); break;
            case '\\': sb.Append('\\'); break;
            case '"' when quoted: sb.Append('"'); break;
            default: sb.Append('\\').Append(c); break;
        }
    }

    // --- Parser ---

    // DatParser.Parse's own loop when root is true, and ReadDictionary's when it is false. The two
    // differ in exactly one thing, and it matters: Parse switches on Key and Comment only, so a
    // CloseDictionary at the root falls through to `default:` and merely advances. The root body has no
    // closer to find and therefore CANNOT end early — a stray '}' half way down a workshop asset drops
    // one token, not the rest of the file.
    private static DatDictionary ParseDictionaryBody(List<Tok> tokens, ref int i, bool root)
    {
        var dict = new DatDictionary();
        while (i < tokens.Count)
        {
            Tok t = tokens[i];
            if (t.Type == TokType.CloseDict && !root)
            {
                i++;
                return dict;
            }
            if (t.Type == TokType.Key)
            {
                i++;
                string? inline = null;
                if (i < tokens.Count && tokens[i].Type == TokType.Value)
                {
                    inline = tokens[i].Text;
                    i++;
                }

                // A '{' or '[' on the following line overrides an inline value — but only on the
                // FOLLOWING line. ReadDictionaryValue advances past at most one line break before it
                // looks for a bracket, so a blank line in between means the bracket is not this key's
                // value at all: the key keeps its inline value (or none), and the block that follows is
                // parsed as if nothing had introduced it.
                int j = i;
                if (j < tokens.Count && tokens[j].Type == TokType.LineBreak) j++;

                if (j < tokens.Count && tokens[j].Type == TokType.OpenDict)
                {
                    i = j + 1;
                    dict.Set(t.Text, ParseDictionaryBody(tokens, ref i, root: false));
                }
                else if (j < tokens.Count && tokens[j].Type == TokType.OpenList)
                {
                    i = j + 1;
                    dict.Set(t.Text, ParseListBody(tokens, ref i));
                }
                else
                {
                    // ReadDictionaryValue: `maybeValueToken.type == Value ? value : null`. A key with no
                    // value at all holds a null, not an empty string, which is why the game reads bare
                    // flags with ContainsKey rather than by parsing them.
                    dict.Set(t.Text, new DatValue(inline));
                }
            }
            else
            {
                i++; // line breaks and stray tokens alike
            }
        }
        return dict;
    }

    private static DatList ParseListBody(List<Tok> tokens, ref int i)
    {
        var list = new DatList();
        while (i < tokens.Count)
        {
            Tok t = tokens[i];
            switch (t.Type)
            {
                case TokType.CloseList:
                    i++;
                    return list;
                case TokType.OpenDict:
                    i++;
                    list.Items.Add(ParseDictionaryBody(tokens, ref i, root: false));
                    break;
                case TokType.OpenList:
                    i++;
                    list.Items.Add(ParseListBody(tokens, ref i));
                    break;
                case TokType.Value:
                    list.Items.Add(new DatValue(t.Text));
                    i++;
                    break;
                default:
                    i++; // line breaks, a stray '}', and keys that cannot occur here
                    break;
            }
        }
        return list;
    }
}
