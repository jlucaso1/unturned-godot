using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Data;
using UnturnedGodot.Repro;
using UnturnedGodot.Zombies;

namespace UnturnedGodot.ReproHarness;

// Replays a bug-repro dump without an engine.
//
//   dotnet run -c Release --project tools/ReproHarness -- info    dump.json
//   dotnet run -c Release --project tools/ReproHarness -- replay  dump.json [--ticks 200] [--level DIR]
//   dotnet run -c Release --project tools/ReproHarness -- verify  dump.json
//   dotnet run -c Release --project tools/ReproHarness -- pretty   dump.json [out.json]
//
// `verify` is the one to run first with a dump someone hands you: it replays the recorded window
// against the code in your working tree and says whether it still comes out the same. On unmodified
// code it should reproduce to the millimetre; once you start changing the brain it will not, and the
// numbers say how far it drifted and how much of the world had to be recomputed from the dump's
// geometry rather than replayed from the recording.
//
// `--level DIR` points at a map folder (the one containing Environment/) so the replay routes over the
// whole navmesh instead of the local slice the dump carries. ./scripts/fetch-game-data.sh downloads one.
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "usage: reproharness <info|replay|verify|pretty> <dump.json[.gz]> [options]");
            return 2;
        }

        string command = args[0];
        string path = args[1];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"no such dump: {path}");
            return 2;
        }

        ReproDump dump;
        try
        {
            dump = ReproDump.Read(path);
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException
            or InvalidDataException)
        {
            Console.Error.WriteLine($"{path} could not be read: {e.Message}");
            return 2;
        }

        return command switch
        {
            "info" => Info(dump),
            "replay" => Replay(dump, args, verify: false),
            "verify" => Replay(dump, args, verify: true),
            "pretty" => Pretty(dump, args),
            _ => Unknown(command),
        };
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command '{command}'");
        return 2;
    }

    private static int Info(ReproDump dump)
    {
        Console.Write(dump.Describe());
        if (dump.Zombies is not { } section)
            return 0;

        // Who was doing something. A dump carries the whole population, and the two or three that were
        // awake are the entire reason it exists.
        Console.WriteLine("\nawake at the start of the window:");
        foreach (ReproZombieRecord zombie in section.State.Zombies)
        {
            if (zombie.State == EZombieState.Idle && zombie.Target == byte.MaxValue)
                continue;
            Console.WriteLine($"  zombie {zombie.Id,-5} {zombie.Speciality,-8} {zombie.State,-7} "
                + $"at {ReproVector.Describe(zombie.Position)} yaw {zombie.Yaw,6:0.#} "
                + $"target {(zombie.Target == byte.MaxValue ? "-" : zombie.Target.ToString())} "
                + $"route {zombie.Route.Length / 3} waypoints"
                + (zombie.PathIsPartial ? " (partial)" : "")
                + (zombie.BlockedRouteTime > 0f ? $" blocked {zombie.BlockedRouteTime:0.##}s" : ""));
        }
        foreach (ReproFrame frame in section.Frames)
        {
            foreach (ReproPlayerSample player in frame.Players)
                Console.WriteLine($"\nplayer {player.Id} at {ReproVector.Describe(player.Position)} "
                    + $"{player.Stance}{(player.Moving ? ", moving" : "")}");
            break;
        }
        if (dump.Log.Count > 0)
        {
            Console.WriteLine("\nlast console lines:");
            for (int i = Math.Max(0, dump.Log.Count - 12); i < dump.Log.Count; i++)
                Console.WriteLine($"  {dump.Log[i]}");
        }
        return 0;
    }

    private static int Replay(ReproDump dump, string[] args, bool verify)
    {
        int extra = Option(args, "--ticks") is { } ticks ? int.Parse(ticks) : 0;
        string? level = Option(args, "--level");
        var options = new ReproScenarioOptions
        {
            UseRecordedAnswers = !Has(args, "--no-oracle"),
            UseGeometry = !Has(args, "--no-geometry"),
            NavFlags = LoadNavmesh(level),
        };

        var scenario = new ReproScenario(dump, options);
        Console.WriteLine($"world: {scenario.Collision?.TriangleCount ?? 0} collision triangles, "
            + $"{(options.NavFlags != null ? "full map navmesh" : "the dump's navmesh slice")}");
        ReproReplayReport report = scenario.Run(extra);
        Console.Write(report.Describe());

        if (!verify)
            return 0;
        if (report.ComparedSamples == 0)
        {
            Console.Error.WriteLine("nothing to verify: the dump has no recorded motion");
            return 1;
        }
        if (!report.ReproducesRecording)
        {
            Console.Error.WriteLine("this build does NOT reproduce the recording");
            return 1;
        }
        Console.WriteLine("this build reproduces the recording");
        return 0;
    }

    private static List<NavFlag>? LoadNavmesh(string? level)
    {
        if (level == null)
            return null;
        string environment = Path.Combine(level, "Environment");
        if (!Directory.Exists(environment))
        {
            Console.Error.WriteLine($"--level {level} has no Environment/ folder; using the dump's slice");
            return null;
        }
        List<NavFlag> flags = LevelNavmesh.Load(environment);
        return flags.Count > 0 ? flags : null;
    }

    private static int Pretty(ReproDump dump, string[] args)
    {
        string output = args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal)
            ? args[2]
            : Path.ChangeExtension(args[1], ".pretty.json");
        dump.Write(output, pretty: true);
        Console.WriteLine($"wrote {output} ({new FileInfo(output).Length / 1024} KB)");
        return 0;
    }

    private static string? Option(string[] args, string name)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == name)
                return args[i + 1];
        return null;
    }

    private static bool Has(string[] args, string name) => Array.IndexOf(args, name) >= 0;
}
