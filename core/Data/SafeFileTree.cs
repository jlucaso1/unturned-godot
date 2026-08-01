using System;
using System.Collections.Generic;
using System.IO;

namespace UnturnedGodot.Data;

// Walks optional game/mod content without letting one unreadable directory hide its readable siblings.
// Directory.EnumerateFiles with AllDirectories cannot provide that isolation: one denied child aborts the
// entire lazy enumeration.
public static class SafeFileTree
{
    public static IReadOnlyList<string> EnumerateFiles(string root, string pattern) =>
        EnumerateFiles(root, pattern,
            (directory, searchPattern) => Directory.GetFiles(directory, searchPattern),
            Directory.GetDirectories);

    internal static IReadOnlyList<string> EnumerateFiles(string root, string pattern,
        Func<string, string, string[]> getFiles, Func<string, string[]> getDirectories)
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            try
            {
                result.AddRange(getFiles(directory, pattern));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Files in this directory are inaccessible, but children may still be readable/listable.
            }

            string[] children;
            try
            {
                children = getDirectories(directory);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue; // isolate this subtree and keep processing its siblings
            }

            // Stack is LIFO; reverse insertion preserves the filesystem provider's stable test order.
            for (int i = children.Length - 1; i >= 0; i--)
                pending.Push(children[i]);
        }

        return result;
    }
}
