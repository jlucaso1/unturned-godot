using System;
using System.IO;

namespace UnturnedGodot.Benchmark;

public static class ProcessMemory
{
    public static long RssBytes()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/self/status"))
            {
                if (!line.StartsWith("VmRSS:", StringComparison.Ordinal)) continue;
                string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 2 && long.TryParse(fields[1], out long kib))
                    return kib * 1024;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess();
            return process.WorkingSet64;
        }
        catch { return 0; }
    }
}
