using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace UnturnedGodot.Data;

// Groups byte-identical files without reading the overwhelmingly common unique-size files. Results keep
// source order, so callers retain deterministic asset ordering while doing expensive preparation once.
public static class ExactFileGroups
{
    public sealed record Group<T>(string ContentKey, string Path, IReadOnlyList<T> Items);

    public static IReadOnlyList<Group<T>> Build<T>(IReadOnlyList<(T Item, string Path)> sources,
        bool deduplicate = true)
    {
        if (!deduplicate)
        {
            var separate = new Group<T>[sources.Count];
            for (int i = 0; i < sources.Count; i++)
                separate[i] = new Group<T>($"source:{i}", sources[i].Path, new[] { sources[i].Item });
            return separate;
        }

        var byLength = new Dictionary<long, List<int>>();
        var keys = new string[sources.Count];
        for (int i = 0; i < sources.Count; i++)
        {
            try
            {
                long length = new FileInfo(sources[i].Path).Length;
                if (!byLength.TryGetValue(length, out List<int>? bucket))
                    byLength[length] = bucket = new List<int>();
                bucket.Add(i);
            }
            catch (IOException) { keys[i] = $"unreadable:{i}"; }
            catch (UnauthorizedAccessException) { keys[i] = $"unreadable:{i}"; }
        }

        Parallel.ForEach(byLength.Values, bucket =>
        {
            if (bucket.Count == 1)
            {
                keys[bucket[0]] = $"unique:{bucket[0]}";
                return;
            }
            foreach (int i in bucket)
            {
                try { keys[i] = ExactContentKey.Bytes(File.ReadAllBytes(sources[i].Path)); }
                catch (IOException) { keys[i] = $"unreadable:{i}"; }
                catch (UnauthorizedAccessException) { keys[i] = $"unreadable:{i}"; }
            }
        });

        var result = new List<Group<T>>();
        var groupByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < sources.Count; i++)
        {
            string key = keys[i] ?? $"unreadable:{i}";
            if (groupByKey.TryGetValue(key, out int groupIndex))
            {
                ((List<T>)result[groupIndex].Items).Add(sources[i].Item);
                continue;
            }
            groupByKey[key] = result.Count;
            result.Add(new Group<T>(key, sources[i].Path, new List<T> { sources[i].Item }));
        }
        return result;
    }
}
