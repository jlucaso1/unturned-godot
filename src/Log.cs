using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot;

// Every console line, stamped with how long the process has been up.
//
// Godot does not timestamp its prints, and almost everything worth reading here is about WHEN something
// happened relative to something else: how long the bundle decode ran before the scene appeared, how
// much of the load the navmesh sync covered, whether a worker was still writing when the game quit.
// Wall-clock time answers none of that at a glance; elapsed time answers all of it by subtraction.
//
//     [  12.480] [stream] scene built: 157 ms
//     [  12.512] [nav] navmesh reconciled with collision: 6131 unwalkable triangles dropped
//
// Width is fixed and the value right-aligned so the messages line up and the eye can run down the column.
// For wall-clock instead, swap Stamp's body for DateTime.Now.ToString("HH:mm:ss.fff").
public static class Log
{
    // The last few hundred lines, for whoever needs the run-up to something rather than the moment of
    // it — today that is the bug-repro capture, which carries this tail into every dump. A fixed ring of
    // strings the logger has already formatted: no extra allocation, and a bounded memory cost.
    private const int TailLines = 256;
    private static readonly string[] Ring = new string[TailLines];
    private static int Written;

    private static string Remember(string line)
    {
        lock (Ring)
        {
            Ring[Written++ % TailLines] = line;
        }
        return line;
    }

    public static List<string> Tail()
    {
        lock (Ring)
        {
            var lines = new List<string>(TailLines);
            int start = Math.Max(0, Written - TailLines);
            for (int i = start; i < Written; i++)
                if (Ring[i % TailLines] is { } line)
                    lines.Add(line);
            return lines;
        }
    }

    // Seconds since the engine started. Time.GetTicksMsec is a plain counter, so this is safe to call
    // from the worker threads that report extraction progress.
    private static string Stamp() => $"[{Time.GetTicksMsec() / 1000.0,8:0.000}] ";

    // Every line as it is written, for whoever wants them live rather than after the fact — today the
    // in-game console, which shows the same stream the terminal gets.
    //
    // Raised on the thread that logged, and that is very often NOT the main one: the bundle decoders, the
    // extraction workers and the foliage streamer all report from a task. A subscriber that touches the
    // engine has to hand the line to the main thread itself; this deliberately does not, because
    // deferring here would put a marshalling cost on every log line in the process for the benefit of a
    // subscriber that usually is not there.
    public static event Action<LogSeverity, string>? LineWritten;

    public static void Print(string message)
    {
        string line = Remember(Stamp() + message);
        GD.Print(line);
        LineWritten?.Invoke(LogSeverity.Info, line);
    }

    public static void PrintErr(string message)
    {
        string line = Remember(Stamp() + message);
        GD.PrintErr(line);
        LineWritten?.Invoke(LogSeverity.Error, line);
    }

    public static void PushWarning(string message)
    {
        string line = Remember(Stamp() + message);
        GD.PushWarning(line);
        LineWritten?.Invoke(LogSeverity.Warning, line);
    }
}

// How loud a log line was, kept alongside the text so a reader can colour or filter without parsing it.
public enum LogSeverity { Info, Warning, Error }
