using System.Collections.Generic;
using System.Text;

namespace UnturnedGodot.Dat;

// Ports Unturned's DatTokenizer + DatParser grammar (UnturnedDat/). Root is always a dictionary.
// Grammar highlights: comments start with '/' to end-of-line; line breaks are significant;
// a dictionary key is the first whitespace-delimited word (or a quoted string) and its value is
// the rest of the line, unless a '{'/'[' opens on the next line (which then wins over an inline value).
public static class DatParser
{
    private enum TokType { Key, Value, OpenDict, CloseDict, OpenList, CloseList, LineBreak }

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
        var contextIsList = new Stack<bool>();
        contextIsList.Push(false);

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
                contextIsList.Push(false);
                p = ConsumeBracket(text, p);
            }
            else if (c == '}')
            {
                tokens.Add(new Tok(TokType.CloseDict));
                if (contextIsList.Count > 1) contextIsList.Pop();
                p = ConsumeBracket(text, p);
            }
            else if (c == '[')
            {
                tokens.Add(new Tok(TokType.OpenList));
                contextIsList.Push(true);
                p = ConsumeBracket(text, p);
            }
            else if (c == ']')
            {
                tokens.Add(new Tok(TokType.CloseList));
                if (contextIsList.Count > 1) contextIsList.Pop();
                p = ConsumeBracket(text, p);
            }
            else if (char.IsWhiteSpace(c) || c == ',')
            {
                p++; // commas are treated as whitespace
            }
            else if (contextIsList.Peek())
            {
                string value = ReadStringValue(text, ref p);
                tokens.Add(new Tok(TokType.Value, value));
            }
            else
            {
                string key = ReadKey(text, ref p);
                tokens.Add(new Tok(TokType.Key, key));
                SkipSpacesAndTabs(text, ref p);
                if (p < n && text[p] != '\r' && text[p] != '\n')
                {
                    string value = ReadStringValue(text, ref p);
                    tokens.Add(new Tok(TokType.Value, value));
                }
            }
        }
        return tokens;
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
        var sb = new StringBuilder();
        while (p < text.Length && text[p] != '\r' && text[p] != '\n')
        {
            char c = text[p];
            if (c == '\\' && p + 1 < text.Length)
            {
                p++;
                sb.Append(Unescape(text[p]));
            }
            else
            {
                sb.Append(c);
            }
            p++;
        }
        return sb.ToString();
    }

    private static string ReadQuoted(string text, ref int p)
    {
        p++; // opening quote
        var sb = new StringBuilder();
        while (p < text.Length && text[p] != '"')
        {
            char c = text[p];
            if (c == '\\' && p + 1 < text.Length)
            {
                p++;
                sb.Append(Unescape(text[p]));
            }
            else
            {
                sb.Append(c);
            }
            p++;
        }
        if (p < text.Length) p++; // closing quote
        if (p < text.Length && text[p] == ',') p++;
        return sb.ToString();
    }

    private static char Unescape(char c) => c switch
    {
        'n' => '\n',
        't' => '\t',
        _ => c, // '\\', '"', and unknown escapes keep the following char
    };

    // --- Parser ---

    private static DatDictionary ParseDictionaryBody(List<Tok> tokens, ref int i, bool root)
    {
        var dict = new DatDictionary();
        while (i < tokens.Count)
        {
            Tok t = tokens[i];
            if (t.Type == TokType.LineBreak) { i++; continue; }
            if (t.Type == TokType.CloseDict)
            {
                if (!root) i++;
                return dict;
            }
            if (t.Type == TokType.Key)
            {
                i++;
                string inline = string.Empty;
                bool hasInline = false;
                if (i < tokens.Count && tokens[i].Type == TokType.Value)
                {
                    inline = tokens[i].Text;
                    hasInline = true;
                    i++;
                }

                // A '{' or '[' on the following line overrides an inline value.
                int j = i;
                while (j < tokens.Count && tokens[j].Type == TokType.LineBreak) j++;
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
                    dict.Set(t.Text, new DatValue(hasInline ? inline : string.Empty));
                }
            }
            else
            {
                i++; // tolerate stray tokens
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
                case TokType.LineBreak:
                    i++;
                    break;
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
                    i++;
                    break;
            }
        }
        return list;
    }
}
