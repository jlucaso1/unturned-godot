using System;
using System.IO;

namespace UnturnedGodot.Data;

// SDG.Unturned.ELevelSize. The map author picks one in the editor; the server reads it to decide how much
// of everything a map gets — vehicles among them.
public enum ELevelSize { Tiny, Small, Medium, Large, Insane }

// Reads the size out of a map's Level.dat, which Unturned writes as a Block: a version byte, the author's
// CSteamID, the size, and (from version 2) the level type. Nothing else in the file is needed here, and a
// map whose file is missing or truncated is treated as Medium, the editor's own default.
public static class LevelSize
{
    private const int SizeOffset = 1 + 8; // version byte + CSteamID

    public static ELevelSize Read(string mapDirectory)
    {
        string path = Path.Combine(mapDirectory, "Level.dat");
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ELevelSize.Medium;
        }

        if (bytes.Length <= SizeOffset)
            return ELevelSize.Medium;

        byte size = bytes[SizeOffset];
        return Enum.IsDefined(typeof(ELevelSize), (int)size) ? (ELevelSize)size : ELevelSize.Medium;
    }
}
