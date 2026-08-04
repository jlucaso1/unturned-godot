using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnturnedGodot.Data;

// Reads the Landscape tile material lists out of Level.hierarchy — Unturned's formatted-KV save (quoted
// strings, {} objects, [] arrays). Only what the splat sampler needs is kept: each tile coordinate's 8
// LandscapeMaterialAsset GUIDs (LandscapeTile.read's "Materials" array). Tiles appear in more than one
// "Tiles" array in the file (the hole data saves coords only); entries carrying Materials win.
public static class LevelHierarchy
{
    public static Dictionary<(int x, int y), Guid[]> ReadTileMaterials(string hierarchyPath)
    {
        var result = new Dictionary<(int, int), Guid[]>();
        if (!File.Exists(hierarchyPath))
            return result;

        List<string> tokens = Tokenize(TextFile.ReadAllText(hierarchyPath));
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] != "\"Coord\"" || i + 1 >= tokens.Count || tokens[i + 1] != "{")
                continue;

            // "Coord" { "X" "n" "Y" "n" } optionally followed by "Materials" [ { "GUID" "..." } x8 ]
            int j = i + 2;
            int? x = null, y = null;
            while (j < tokens.Count && tokens[j] != "}")
            {
                if (tokens[j] == "\"X\"" && j + 1 < tokens.Count)
                    x = ParseInt(tokens[j + 1]);
                else if (tokens[j] == "\"Y\"" && j + 1 < tokens.Count)
                    y = ParseInt(tokens[j + 1]);
                j++;
            }
            if (x == null || y == null)
                continue;

            if (j + 2 < tokens.Count && tokens[j + 1] == "\"Materials\"" && tokens[j + 2] == "[")
            {
                var guids = new List<Guid>(SplatmapTile.LAYERS);
                int k = j + 3;
                int depth = 1;
                while (k < tokens.Count && depth > 0)
                {
                    if (tokens[k] == "[")
                        depth++;
                    else if (tokens[k] == "]")
                        depth--;
                    else if (tokens[k] == "\"GUID\"" && k + 1 < tokens.Count)
                        guids.Add(Guid.TryParse(Unquote(tokens[k + 1]), out Guid g) ? g : Guid.Empty);
                    k++;
                }
                result[(x.Value, y.Value)] = guids.ToArray();
            }
        }
        return result;
    }

    private static int? ParseInt(string token) =>
        int.TryParse(Unquote(token), out int v) ? v : null;

    // Tokens are either quoted strings (length >= 2 by construction) or single-char brackets.
    private static string Unquote(string token) =>
        token[0] == '"' ? token[1..^1] : token;

    // Quoted strings and the four structural brackets; everything else in the file is whitespace.
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                sb.Clear().Append('"');
                for (i++; i < text.Length && text[i] != '"'; i++)
                    sb.Append(text[i]);
                tokens.Add(sb.Append('"').ToString());
            }
            else if (c is '{' or '}' or '[' or ']')
            {
                tokens.Add(c.ToString());
            }
        }
        return tokens;
    }
}
