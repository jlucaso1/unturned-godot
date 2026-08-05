using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;

namespace UnturnedGodot;

// Runtime-safe renderer shared by the editor navigation preview and automated screenshots. Keeping the
// geometry here means an X-ray comparison is the same overlay with one material property changed, rather
// than two diagnostic paths that can disagree about which triangles or rim edges exist.
public static class NavigationOverlay
{
    public const float DefaultLift = 0.55f;
    public const string SurfaceNode = "Surface";
    public const string RimNode = "Rim";
    public const string BeaconNode = "Beacons";
    public const string BoundsNode = "Bounds";

    private const float SurfaceAlpha = 0.5f;
    private const float BeaconHeight = 120f;
    private const float BeaconSolidShare = 0.6f;
    private const int MaxBeacons = 4096;

    public static Node3D Build(IReadOnlyList<NavFlag> flags, IReadOnlyList<NavFlagSurvey> survey,
        IReadOnlyList<NavBound> spawnBounds, string rootName, bool rim, bool beacons, bool bounds,
        bool xray, float lift)
    {
        var root = new Node3D { Name = rootName, Position = new Vector3(0, lift, 0) };
        var surfaces = new Node3D { Name = SurfaceNode };
        var rims = new Node3D { Name = RimNode, Visible = rim };
        root.AddChild(surfaces);
        root.AddChild(rims);

        StandardMaterial3D surfaceMaterial = Material(xray, lines: false);
        StandardMaterial3D rimMaterial = Material(xray, lines: true);
        for (int i = 0; i < survey.Count; i++)
        {
            if (BuildSurface(survey[i], surfaceMaterial, $"Flag{i}") is { } surface)
                surfaces.AddChild(surface);
            if (BuildRim(survey[i], rimMaterial, $"Flag{i}") is { } edge)
                rims.AddChild(edge);
        }
        if (BuildBeacons(survey, out _, out _) is { } beaconLayer)
        {
            beaconLayer.Visible = beacons;
            root.AddChild(beaconLayer);
        }
        if (BuildBounds(flags, spawnBounds) is { } boundsLayer)
        {
            boundsLayer.Visible = bounds;
            root.AddChild(boundsLayer);
        }
        return root;
    }

    // Expanded to three vertices per face: disconnected islands may meet at one authored vertex, and an
    // indexed vertex cannot carry both island colours.
    public static MeshInstance3D? BuildSurface(NavFlagSurvey flag, Material material, string name)
    {
        int faces = 0;
        foreach (int island in flag.IslandOfTriangle)
            if (island >= 0)
                faces++;
        if (faces == 0)
            return null;

        var vertices = new Vector3[faces * 3];
        var colors = new Color[faces * 3];
        int at = 0;
        for (int triangle = 0; triangle < flag.IslandOfTriangle.Length; triangle++)
        {
            int island = flag.IslandOfTriangle[triangle];
            if (island < 0)
                continue;
            Color colour = IslandColour(island);
            for (int corner = 0; corner < 3; corner++)
            {
                vertices[at] = flag.Flag.Vertices[flag.Flag.Triangles[(triangle * 3) + corner]];
                colors[at] = colour;
                at++;
            }
        }
        return Instance(name, Mesh.PrimitiveType.Triangles, vertices, colors, material);
    }

    public static MeshInstance3D? BuildRim(NavFlagSurvey flag, Material material, string name)
    {
        if (flag.Rim.Count == 0)
            return null;

        var vertices = new Vector3[flag.Rim.Count * 2];
        var colors = new Color[flag.Rim.Count * 2];
        for (int i = 0; i < flag.Rim.Count; i++)
        {
            vertices[i * 2] = flag.Rim[i].A;
            vertices[(i * 2) + 1] = flag.Rim[i].B;
            colors[i * 2] = RimColour;
            colors[(i * 2) + 1] = RimColour;
        }
        return Instance(name, Mesh.PrimitiveType.Lines, vertices, colors, material);
    }

    public static MeshInstance3D? BuildBeacons(IReadOnlyList<NavFlagSurvey> survey, out int drawn,
        out int total)
    {
        var vertices = new List<Vector3>();
        var colors = new List<Color>();
        total = 0;
        drawn = 0;
        foreach (NavFlagSurvey flag in survey)
            for (int island = 1; island < flag.Islands.Count; island++)
            {
                total++;
                if (drawn >= MaxBeacons)
                    continue;
                drawn++;
                Vector3 anchor = flag.Islands[island].Anchor;
                Color colour = IslandColour(island) with { A = 1f };
                Vector3 fadeFrom = anchor + new Vector3(0, BeaconHeight * BeaconSolidShare, 0);
                vertices.Add(anchor);
                vertices.Add(fadeFrom);
                colors.Add(colour);
                colors.Add(colour);
                vertices.Add(fadeFrom);
                vertices.Add(anchor + new Vector3(0, BeaconHeight, 0));
                colors.Add(colour);
                colors.Add(colour with { A = 0f });
            }

        return drawn == 0
            ? null
            : Instance(BeaconNode, Mesh.PrimitiveType.Lines, vertices.ToArray(), colors.ToArray(),
                Material(xray: true, lines: true));
    }

    public static MeshInstance3D? BuildBounds(IReadOnlyList<NavFlag> flags,
        IReadOnlyList<NavBound> spawnBounds)
    {
        var vertices = new List<Vector3>();
        var colors = new List<Color>();
        foreach (NavFlag flag in flags)
            AppendBox(vertices, colors, flag.Center, flag.Size, NavmeshBoxColour);
        foreach (NavBound bound in spawnBounds)
            AppendBox(vertices, colors, bound.Center, bound.Size, SpawnBoxColour);
        return vertices.Count == 0
            ? null
            : Instance(BoundsNode, Mesh.PrimitiveType.Lines, vertices.ToArray(), colors.ToArray(),
                Material(xray: true, lines: true));
    }

    private static void AppendBox(List<Vector3> vertices, List<Color> colors, Vector3 centre,
        Vector3 size, Color colour)
    {
        Vector3 half = size * 0.5f;
        Span<Vector3> corner = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
            corner[i] = centre + new Vector3(
                (i & 1) == 0 ? -half.X : half.X,
                (i & 2) == 0 ? -half.Y : half.Y,
                (i & 4) == 0 ? -half.Z : half.Z);
        ReadOnlySpan<int> edges = stackalloc int[]
        {
            0, 1, 2, 3, 4, 5, 6, 7,
            0, 2, 1, 3, 4, 6, 5, 7,
            0, 4, 1, 5, 2, 6, 3, 7,
        };
        foreach (int index in edges)
        {
            vertices.Add(corner[index]);
            colors.Add(colour);
        }
    }

    public static MeshInstance3D Instance(string name, Mesh.PrimitiveType primitive,
        Vector3[] vertices, Color[] colors, Material material)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(primitive, arrays);
        return new MeshInstance3D
        {
            Name = name,
            Mesh = mesh,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
        };
    }

    public static StandardMaterial3D Material(bool xray, bool lines) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        NoDepthTest = xray,
        DisableFog = true,
        DisableReceiveShadows = true,
        RenderPriority = lines ? 1 : 0,
    };

    public static Color IslandColour(int index) => index == 0
        ? new Color(0.35f, 0.62f, 0.95f, SurfaceAlpha)
        : Color.FromHsv((0.08f + (index * 0.6180339f)) % 1f, 0.95f, 1f, SurfaceAlpha);

    public static readonly Color RimColour = new(1f, 0.25f, 0.2f, 0.9f);
    private static readonly Color NavmeshBoxColour = new(1f, 0.85f, 0.2f, 0.55f);
    private static readonly Color SpawnBoxColour = new(0.85f, 0.3f, 0.9f, 0.35f);
}
