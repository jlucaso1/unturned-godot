using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnturnedGodot.Player;
using UnturnedGodot.Zombies;

namespace UnturnedGodot.Repro;

// The on-disk bug report: enough of the running game to put it back where it was when someone pressed
// the capture key, and to run it forward again — headless, in the editor, or in a test — and watch the
// same thing happen.
//
// It is deliberately NOT a zombie-AI format. A dump is a slice of the session, and each subsystem
// contributes the part it owns:
//
//   meta      what/where/when, the camera pose, the reporter's note, whatever the capture warns about
//   session   the players (full authoritative movement state), the server tick, time of day
//   world     level identity, a LOCAL slice of the collision geometry, navmesh and heightfield
//   zombies   the AI section: complete brain state, the world's answers, the trajectory that followed
//   log       the tail of the console, because half of every bug report is already in there
//   sections  anything a future subsystem wants to add, as raw JSON, without this file changing
//
// The state in every section is from the FIRST tick of the window, not the last. A dump you cannot
// watch develop is a screenshot, and screenshots are what bug reports already are.
public sealed record ReproDump
{
    public const string CurrentSchemaVersion = "1";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public ReproMeta Meta { get; init; } = new();
    public ReproSessionData Session { get; init; } = new();
    public ReproWorldData World { get; init; } = new();
    public ReproZombieSection? Zombies { get; init; }
    public List<string> Log { get; init; } = new();

    // Extension point for subsystems this file has never heard of: a provider hands the capture a name
    // and its own serialized payload, and it rides along untouched. Adding a section is then a change
    // to that subsystem, not to the dump format or to every reader of it.
    public Dictionary<string, JsonElement> Sections { get; init; } = new();

    public string ToJson(bool pretty = false) => JsonSerializer.Serialize(this,
        pretty ? ReproJsonPretty.Default.ReproDump : ReproJson.Default.ReproDump);

    public static ReproDump? FromJson(string json) =>
        JsonSerializer.Deserialize(json, ReproJson.Default.ReproDump);

    // Writes the dump, gzipping when the path asks for it (".gz"). Plain JSON is the default because
    // the first thing anyone does with one of these is read it or hand it to an agent; a big window
    // compresses to a fraction of that when it has to travel.
    public void Write(string path, bool pretty = false)
    {
        string json = ToJson(pretty);
        if (!IsGzip(path))
        {
            File.WriteAllText(path, json);
            return;
        }
        using FileStream file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        using var writer = new StreamWriter(gzip);
        writer.Write(json);
    }

    public static ReproDump Read(string path)
    {
        string json;
        if (IsGzip(path))
        {
            using FileStream file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            json = reader.ReadToEnd();
        }
        else
        {
            json = File.ReadAllText(path);
        }
        return FromJson(json)
            ?? throw new InvalidDataException($"{path} does not contain a repro dump.");
    }

    private static bool IsGzip(string path) => path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);

    // The one-screen summary. Printed by the harness, and on the type itself because the first
    // question about any dump is "what is even in it".
    public string Describe()
    {
        var text = new System.Text.StringBuilder();
        text.Append("repro dump v").Append(SchemaVersion).Append(" — ").Append(Meta.Map)
            .Append(" @ ").Append(Meta.CapturedAtUtc).Append('\n');
        text.Append("  reason      ").Append(Meta.Reason).Append('\n');
        if (!string.IsNullOrEmpty(Meta.Note))
            text.Append("  note        ").Append(Meta.Note).Append('\n');
        text.Append("  window      ").Append(Meta.TickCount).Append(" ticks from ")
            .Append(Meta.StartTick).Append(" (")
            .Append(Format(Meta.TickCount * Meta.TickRate)).Append(" s)\n");
        text.Append("  focus       ").Append(ReproVector.Describe(Meta.FocusPoint)).Append('\n');
        if (!string.IsNullOrEmpty(Meta.ShotCam))
            text.Append("  camera      SHOT_CAM=").Append(Meta.ShotCam).Append('\n');
        text.Append("  players     ").Append(Session.Players.Count).Append('\n');
        text.Append("  world       ").Append(World.HasNavmesh
                ? $"{World.NavFlags.Count} nav flags, {World.SlicedTriangleCount} navmesh triangles"
                : "(this map ships no navmesh)")
            .Append(World.Geometry == null
                ? ", no collision slice"
                : $", {World.Geometry.TriangleCount} collision triangles within "
                    + $"{Format(World.Geometry.Radius)} m")
            .Append('\n');
        if (Zombies != null)
            text.Append(Zombies.Describe());
        foreach (string name in Sections.Keys)
            text.Append("  section     ").Append(name).Append('\n');
        foreach (string warning in Meta.Warnings)
            text.Append("  ! ").Append(warning).Append('\n');
        return text.ToString();
    }

    internal static string Format(float value) =>
        value.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record ReproMeta
{
    public string CapturedAtUtc { get; init; } = "";
    public string Reason { get; init; } = "manual";
    public string Note { get; init; } = "";
    public string Map { get; init; } = "";
    public string LevelName { get; init; } = "";
    public string Engine { get; init; } = "";
    public string Build { get; init; } = "";
    public bool Headless { get; init; }
    public float TickRate { get; init; } = 0.08f;
    public uint StartTick { get; init; }
    public int TickCount { get; init; }

    // Where the interesting thing was. The focus drives every radius in the capture: which geometry,
    // which navmesh and which entities are worth keeping.
    public float[] FocusPoint { get; init; } = Array.Empty<float>();
    public float FocusRadius { get; init; }

    // "x,y,z,pitch,yaw" — paste into SHOT_CAM to render the reporter's exact view again.
    public string ShotCam { get; init; } = "";

    public List<string> Warnings { get; init; } = new();
}

// The session, as any subsystem's bug would want it: who was where, doing what, at which tick.
public sealed record ReproSessionData
{
    public uint ServerTick { get; init; }
    public float TimeOfDay { get; init; } = -1f; // -1: not captured
    public List<ReproPlayerState> Players { get; init; } = new();
}

// PlayerMoveState, whole. Position alone reproduces where someone stood; velocity, grounding and the
// look angles reproduce what they were in the middle of doing.
public sealed record ReproPlayerState
{
    public byte Id { get; init; }
    public string Name { get; init; } = "";
    public float[] Position { get; init; } = Array.Empty<float>();
    public float[] Velocity { get; init; } = Array.Empty<float>();
    public bool Grounded { get; init; }
    public float Yaw { get; init; }
    public float Pitch { get; init; }
    public EPlayerStance Stance { get; init; }
    public bool Moving { get; init; }
}

public sealed record ReproWorldData
{
    public List<ReproNavBound> Bounds { get; init; } = new();
    public List<ReproZombieTable> Tables { get; init; } = new();

    // Whether the SESSION had a navmesh at all. Not the same question as "are there triangles in this
    // dump": with no navmesh the brain falls back to territory bounds for checkNavigation and for the
    // player's nav region, which is a different simulation, not a less detailed one.
    public bool HasNavmesh { get; init; }
    public List<ReproNavFlag> NavFlags { get; init; } = new();

    // A local patch of the terrain heightfield: the ground the simulation falls back to when nothing
    // else answers, sampled on a regular grid around the focus.
    public ReproHeightPatch? Ground { get; init; }

    // The collision triangles around the incident (see ReproGeometryData).
    public ReproGeometryData? Geometry { get; init; }

    public int SlicedTriangleCount
    {
        get
        {
            int total = 0;
            foreach (ReproNavFlag flag in NavFlags)
                total += flag.Triangles.Length / 3;
            return total;
        }
    }
}

public sealed record ReproNavBound
{
    public float[] Center { get; init; } = Array.Empty<float>();
    public float[] Size { get; init; } = Array.Empty<float>();
    public byte MaxZombies { get; init; }
    public bool SpawnZombies { get; init; }
    public bool HyperAgro { get; init; }
}

public sealed record ReproZombieTable
{
    public string Name { get; init; } = "";
    public bool IsMega { get; init; }
    public ushort Health { get; init; }
    public byte Damage { get; init; }
}

// One nav flag. Center/Size are always the real ones (they decide checkNavigation and the player's nav
// region, and cost three floats each), while the triangles are only kept near the focus.
public sealed record ReproNavFlag
{
    public float[] Center { get; init; } = Array.Empty<float>();
    public float[] Size { get; init; } = Array.Empty<float>();
    public float[] Vertices { get; init; } = Array.Empty<float>();
    public int[] Triangles { get; init; } = Array.Empty<int>();
    public bool Sliced { get; init; }
}

public sealed record ReproHeightPatch
{
    public float[] Origin { get; init; } = Array.Empty<float>(); // world XZ of cell (0,0)
    public float Cell { get; init; } = 1f;
    public int Width { get; init; }
    public int Depth { get; init; }
    public float[] Heights { get; init; } = Array.Empty<float>(); // row-major, x fastest; NaN: no ground
}

// The collision world around the incident, as a triangle soup in world space. Every collider shape the
// engine had there is tessellated into triangles at capture time, which is what lets the replay run one
// piece of geometry code instead of one per shape kind — and lets a dump replay in a build that has
// never heard of whatever shape produced it.
public sealed record ReproGeometryData
{
    public float[] Center { get; init; } = Array.Empty<float>();
    public float Radius { get; init; }
    public float[] Vertices { get; init; } = Array.Empty<float>(); // flat xyz
    public int[] Triangles { get; init; } = Array.Empty<int>();    // 3 indices each
    public int[] Layers { get; init; } = Array.Empty<int>();       // per triangle: the body's layer mask
    public int[] Owners { get; init; } = Array.Empty<int>();       // per triangle: index into OwnerNames
    public List<string> OwnerNames { get; init; } = new();

    public int TriangleCount => Triangles.Length / 3;
}

// Source-generated serialization: AOT/trim-safe (the game publishes with PublishAot=true), no external
// dependencies. Defaults are omitted because a dump is mostly idle entities whose fields are all zero,
// and the named-literal handling exists for ZombieInstance.SinceSwing, which starts at +infinity.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage] // source-generated serialization plumbing
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ReproDump))]
public partial class ReproJson : JsonSerializerContext
{
}

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ReproDump))]
public partial class ReproJsonPretty : JsonSerializerContext
{
}
