using System;
using Godot;

namespace UnturnedGodot.RuntimeTests;

// The three things almost every runtime test needs, in one place.
//
// They were copied per file and the copies had drifted, which matters more than duplication usually does
// because each one decides whether a test RUNS. One probe checked for the install root and another for
// Maps/PEI; one reported "no Unturned install" and another "no PEI map"; and UG_REQUIRE_REAL_DATA — the
// flag whose whole job is to turn a silent skip into a failure — was honoured by some and not others. A
// suite where the skip rules disagree reports green for different reasons in different files.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class RealData
{
    // The installed game, or nothing.
    //
    // Under UG_REQUIRE_REAL_DATA=1 the absence is a FAILURE rather than a skip: that flag exists so the
    // job which fetches the content cannot pass by finding none, and a test that skipped there would
    // report the fetch as working while measuring nothing.
    public static bool Install(out string install)
    {
        install = "";
        string? found = Assets.UnturnedInstall.Find();
        if (found == null)
            return Missing("no Unturned install", out install);

        install = found;
        return true;
    }

    // One map's directory inside that install.
    public static bool Map(string name, out string install, out Data.LevelInfo level)
    {
        level = null!;
        if (!Install(out install))
            return false;

        string maps = System.IO.Path.Combine(install, "Maps", name);
        if (!System.IO.Directory.Exists(maps))
            return Missing($"no {name} map", out install);

        level = new Data.LevelInfo(maps);
        return true;
    }

    // PEI, which is what the content fetch downloads and therefore the only map a required-real-data run
    // may insist on.
    public static bool Pei(out string install, out Data.LevelInfo level) => Map("PEI", out install, out level);

    // The core masterbundle, which everything with a texture or a sound in it comes out of.
    public static bool MasterBundle(out string bundle)
    {
        bundle = "";
        if (!Install(out string install))
            return false;

        string? found = Assets.UnturnedInstall.FindMasterBundle(install);
        if (found == null)
            return Missing("the core masterbundle", out bundle);

        bundle = found;
        return true;
    }

    private static bool Missing(string what, out string nothing)
    {
        nothing = "";
        if (System.Environment.GetEnvironmentVariable("UG_REQUIRE_REAL_DATA") == "1")
        {
            throw new System.IO.IOException(
                $"UG_REQUIRE_REAL_DATA=1 but {what} is present; this run exists to prove these tests "
                + "execute against the fetched content");
        }

        Log.Print($"[runtime-tests] skipping: {what} "
            + "(set UNTURNED_PATH or run ./scripts/fetch-game-data.sh)");
        return false;
    }

    // A UDP port the OS says is free.
    //
    // Hard-coded numbers turn "nobody is listening" into an assumption about the machine, and several of
    // these tests depend on it being a fact — a server binds immediately, so an occupied port fails the
    // test at construction rather than at its assertion, and two suite processes on one host collide.
    public static ushort FreePort()
    {
        using var probe = new System.Net.Sockets.UdpClient(0, System.Net.Sockets.AddressFamily.InterNetwork);
        return (ushort)((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    // One environment variable, set for a scope and put back afterwards. Left set, the next test in the
    // process runs under this one's answer — and these flags change which code path is taken at all.
    public sealed class EnvScope : IDisposable
    {
        private readonly string _name;
        private readonly string _previous;

        public EnvScope(string name, string value)
        {
            _name = name;
            _previous = OS.GetEnvironment(name);
            OS.SetEnvironment(name, value);
        }

        public void Dispose() => OS.SetEnvironment(_name, _previous);
    }

    // A temporary directory the test owns.
    //
    // Dispose swallows UnauthorizedAccessException as well as IOException: a read-only or locked entry
    // throws the former, and an escaping exception from cleanup fails an otherwise passing test — which
    // is the opposite of what "a leftover temp directory is not worth failing a test over" means.
    public sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir(string prefix = "unturned-godot-test")
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                prefix + "-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path);
        }

        public void Dispose() => Remove(Path);

        public static void Remove(string directory)
        {
            try
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
            {
                // Cleanup is best effort by design.
            }
        }

        public static void RemoveFile(string path)
        {
            try
            {
                System.IO.File.Delete(path);
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }
}
