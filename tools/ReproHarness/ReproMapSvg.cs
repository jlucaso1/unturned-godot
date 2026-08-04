using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using Godot;
using UnturnedGodot.Repro;
using UnturnedGodot.Zombies;

namespace UnturnedGodot.ReproHarness;

// A dependency-free top-down incident map. SVG keeps every triangle and label inspectable at any
// zoom, opens in a browser, and can be attached to an issue without needing Godot or Python.
internal static class ReproMapSvg
{
    private const int PlotSize = 1000;
    private const float Margin = 28f;
    private static readonly string[] ZombieColours =
    {
        "#f59e0b", "#ec4899", "#a78bfa", "#22d3ee", "#fb7185", "#facc15",
        "#2dd4bf", "#c084fc",
    };

    internal static string Render(ReproDump dump, IReadOnlyList<ZombieInstance>? liveZombies,
        int ticks, float? requestedRadius)
    {
        ArgumentNullException.ThrowIfNull(dump);
        Vector3 focus = ReproVector.To(dump.Meta.FocusPoint);
        float radius = requestedRadius ?? dump.World.Geometry?.Radius ?? dump.Meta.FocusRadius;
        if (!float.IsFinite(radius) || radius <= 0f)
            radius = 16f;
        float scale = (PlotSize - (Margin * 2f)) / (radius * 2f);

        var svg = new StringBuilder(256 * 1024);
        svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1310\" height=\"1000\" ")
            .Append("viewBox=\"0 0 1310 1000\">\n")
            .Append("<rect width=\"1310\" height=\"1000\" fill=\"#07111f\"/>\n")
            .Append("<defs><pattern id=\"disabled\" width=\"7\" height=\"7\" ")
            .Append("patternUnits=\"userSpaceOnUse\" patternTransform=\"rotate(45)\">")
            .Append("<rect width=\"7\" height=\"7\" fill=\"#7f1d1d\" fill-opacity=\".36\"/>")
            .Append("<line x1=\"0\" y1=\"0\" x2=\"0\" y2=\"7\" stroke=\"#fb923c\" ")
            .Append("stroke-opacity=\".7\" stroke-width=\"2\"/></pattern>")
            .Append("<filter id=\"halo\"><feGaussianBlur stdDeviation=\"2.5\" result=\"blur\"/>")
            .Append("<feMerge><feMergeNode in=\"blur\"/><feMergeNode in=\"SourceGraphic\"/></feMerge>")
            .Append("</filter><clipPath id=\"plotClip\"><rect x=\"28\" y=\"28\" width=\"944\" ")
            .Append("height=\"944\" rx=\"8\"/></clipPath></defs>\n")
            .Append("<rect x=\"28\" y=\"28\" width=\"944\" height=\"944\" rx=\"8\" ")
            .Append("fill=\"#0b1728\" stroke=\"#334155\"/>\n")
            .Append("<g clip-path=\"url(#plotClip)\">\n");

        DrawGrid(svg, focus, radius, scale);
        DrawNavmesh(svg, dump.World.NavFlags, focus, radius, scale);
        DrawCollision(svg, dump.World.Geometry, focus, radius, scale);
        DrawRoutesAndActors(svg, dump, liveZombies, ticks, focus, radius, scale);
        svg.Append("</g>\n");
        DrawPanel(svg, dump, liveZombies, ticks, focus, radius);
        svg.Append("</svg>\n");
        return svg.ToString();
    }

    private static void DrawGrid(StringBuilder svg, Vector3 focus, float radius, float scale)
    {
        float spacing = radius <= 10f ? 1f : radius <= 24f ? 2f : 5f;
        float minX = focus.X - radius, maxX = focus.X + radius;
        float minZ = focus.Z - radius, maxZ = focus.Z + radius;
        float firstX = MathF.Ceiling(minX / spacing) * spacing;
        float firstZ = MathF.Ceiling(minZ / spacing) * spacing;
        svg.Append("<g stroke=\"#64748b\" stroke-opacity=\".14\" stroke-width=\"1\">\n");
        for (float x = firstX; x <= maxX; x += spacing)
        {
            float sx = X(x, focus, radius, scale);
            svg.Append("<line x1=\"").Append(F(sx)).Append("\" y1=\"").Append(F(Margin))
                .Append("\" x2=\"").Append(F(sx)).Append("\" y2=\"")
                .Append(F(PlotSize - Margin)).Append("\"/>\n");
        }
        for (float z = firstZ; z <= maxZ; z += spacing)
        {
            float sy = Y(z, focus, radius, scale);
            svg.Append("<line x1=\"").Append(F(Margin)).Append("\" y1=\"").Append(F(sy))
                .Append("\" x2=\"").Append(F(PlotSize - Margin)).Append("\" y2=\"")
                .Append(F(sy)).Append("\"/>\n");
        }
        svg.Append("</g>\n");
    }

    private static void DrawNavmesh(StringBuilder svg, IReadOnlyList<ReproNavFlag> flags,
        Vector3 focus, float radius, float scale)
    {
        svg.Append("<g stroke-linejoin=\"round\">\n");
        foreach (ReproNavFlag flag in flags)
        {
            var disabled = new HashSet<int>(flag.DisabledFaces);
            for (int face = 0; face * 3 + 2 < flag.Triangles.Length; face++)
            {
                int ia = flag.Triangles[face * 3], ib = flag.Triangles[(face * 3) + 1];
                int ic = flag.Triangles[(face * 3) + 2];
                if (!TryVertex(flag.Vertices, ia, out Vector3 a)
                    || !TryVertex(flag.Vertices, ib, out Vector3 b)
                    || !TryVertex(flag.Vertices, ic, out Vector3 c)
                    || !InView(a, b, c, focus, radius))
                    continue;
                string fill = disabled.Contains(face) ? "url(#disabled)" : "#22c55e";
                string stroke = disabled.Contains(face) ? "#fb923c" : "#86efac";
                string opacity = disabled.Contains(face) ? ".72" : ".30";
                Polygon(svg, a, b, c, focus, radius, scale, fill, stroke, opacity, ".45");
            }
        }
        svg.Append("</g>\n");
    }

    private static void DrawCollision(StringBuilder svg, ReproGeometryData? geometry,
        Vector3 focus, float radius, float scale)
    {
        if (geometry == null)
            return;
        svg.Append("<g fill=\"none\" stroke-linejoin=\"round\" stroke-linecap=\"round\">\n");
        for (int face = 0; face * 3 + 2 < geometry.Triangles.Length; face++)
        {
            int ia = geometry.Triangles[face * 3], ib = geometry.Triangles[(face * 3) + 1];
            int ic = geometry.Triangles[(face * 3) + 2];
            if (!TryVertex(geometry.Vertices, ia, out Vector3 a)
                || !TryVertex(geometry.Vertices, ib, out Vector3 b)
                || !TryVertex(geometry.Vertices, ic, out Vector3 c)
                || !InView(a, b, c, focus, radius))
                continue;
            float minY = MathF.Min(a.Y, MathF.Min(b.Y, c.Y));
            float maxY = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
            Vector3 normal = (b - a).Cross(c - a);
            float length = normal.Length();
            bool wall = length > 1e-5f && MathF.Abs(normal.Y) / length < 0.68f
                && minY <= focus.Y + 2.1f && maxY >= focus.Y + 0.15f;
            if (!wall)
                continue;
            Polygon(svg, a, b, c, focus, radius, scale, "#ef4444", "#fecaca", ".34", "1.15");
        }
        svg.Append("</g>\n");
    }

    private static void DrawRoutesAndActors(StringBuilder svg, ReproDump dump,
        IReadOnlyList<ZombieInstance>? live, int ticks, Vector3 focus, float radius, float scale)
    {
        ReproPlayerSample? player = null;
        if (dump.Zombies is { Frames.Count: > 0 } section)
        {
            int frame = Math.Min(ticks, section.Frames.Count - 1);
            if (section.Frames[frame].Players.Count > 0)
                player = section.Frames[frame].Players[0];
        }
        Vector3 playerPosition = player == null ? Vector3.Zero : ReproVector.To(player.Position);

        var actors = new List<Actor>();
        if (live != null)
        {
            foreach (ZombieInstance zombie in live)
                if (zombie.TargetPlayer != byte.MaxValue || zombie.State != EZombieState.Idle)
                    actors.Add(new Actor(zombie.Id, zombie.State, zombie.TargetPlayer, zombie.Position,
                        zombie.PathPoints));
        }
        else if (dump.Zombies is { } zombies)
        {
            foreach (ReproZombieRecord zombie in zombies.State.Zombies)
                if (zombie.Target != byte.MaxValue || zombie.State != EZombieState.Idle)
                {
                    var route = new List<Vector3>();
                    ReproVector.Unflatten(zombie.Route, route);
                    actors.Add(new Actor(zombie.Id, zombie.State, zombie.Target,
                        ReproVector.To(zombie.Position), route));
                }
        }

        for (int i = 0; i < actors.Count; i++)
        {
            Actor actor = actors[i];
            string colour = ZombieColours[i % ZombieColours.Length];
            if (actor.Route.Count > 1)
            {
                svg.Append("<polyline points=\"");
                foreach (Vector3 point in actor.Route)
                    svg.Append(F(X(point.X, focus, radius, scale))).Append(',')
                        .Append(F(Y(point.Z, focus, radius, scale))).Append(' ');
                svg.Append("\" fill=\"none\" stroke=\"").Append(colour)
                    .Append("\" stroke-width=\"3\" stroke-opacity=\".82\" ")
                    .Append("stroke-dasharray=\"8 5\"/>\n");
                for (int p = 1; p < actor.Route.Count; p++)
                    Circle(svg, actor.Route[p], 3.2f, colour, "#07111f", focus, radius, scale);
            }
        }

        if (player != null)
        {
            float px = X(playerPosition.X, focus, radius, scale);
            float py = Y(playerPosition.Z, focus, radius, scale);
            svg.Append("<circle cx=\"").Append(F(px)).Append("\" cy=\"").Append(F(py))
                .Append("\" r=\"10\" fill=\"#2563eb\" stroke=\"#dbeafe\" stroke-width=\"3\" ")
                .Append("filter=\"url(#halo)\"/>\n")
                .Append("<text x=\"").Append(F(px + 13f)).Append("\" y=\"").Append(F(py - 11f))
                .Append("\" fill=\"#dbeafe\" font-family=\"monospace\" font-size=\"15\" ")
                .Append("font-weight=\"700\">PLAYER ").Append(player.Id).Append("</text>\n");
        }

        for (int i = 0; i < actors.Count; i++)
        {
            Actor actor = actors[i];
            string colour = ZombieColours[i % ZombieColours.Length];
            Circle(svg, actor.Position, 8f, colour, "#fff7ed", focus, radius, scale);
            float x = X(actor.Position.X, focus, radius, scale);
            float y = Y(actor.Position.Z, focus, radius, scale);
            svg.Append("<text x=\"").Append(F(x + 11f)).Append("\" y=\"").Append(F(y + 5f))
                .Append("\" fill=\"").Append(colour)
                .Append("\" stroke=\"#07111f\" stroke-width=\"3\" paint-order=\"stroke\" ")
                .Append("font-family=\"monospace\" font-size=\"14\" font-weight=\"700\">z")
                .Append(actor.Id).Append(' ').Append(actor.State).Append("</text>\n");
        }
    }

    private static void DrawPanel(StringBuilder svg, ReproDump dump,
        IReadOnlyList<ZombieInstance>? live, int ticks, Vector3 focus, float radius)
    {
        int active = 0;
        if (live != null)
        {
            foreach (ZombieInstance zombie in live)
                if (zombie.TargetPlayer != byte.MaxValue || zombie.State != EZombieState.Idle)
                    active++;
        }
        else if (dump.Zombies is { } section)
        {
            foreach (ReproZombieRecord recorded in section.State.Zombies)
                if (recorded.Target != byte.MaxValue || recorded.State != EZombieState.Idle)
                    active++;
        }

        int navFaces = dump.World.SlicedTriangleCount;
        int disabled = 0;
        foreach (ReproNavFlag flag in dump.World.NavFlags)
            disabled += flag.DisabledFaces.Length;
        int collision = dump.World.Geometry?.TriangleCount ?? 0;
        float x = PlotSize + 24f;
        svg.Append("<g font-family=\"Inter,system-ui,sans-serif\" fill=\"#e2e8f0\">\n")
            .Append("<text x=\"").Append(F(x)).Append("\" y=\"54\" font-size=\"22\" ")
            .Append("font-weight=\"800\">REPRO MAP</text>\n")
            .Append("<text x=\"").Append(F(x)).Append("\" y=\"82\" font-size=\"15\" ")
            .Append("fill=\"#93c5fd\" font-weight=\"700\">").Append(E(dump.Meta.Map))
            .Append("</text>\n")
            .Append("<text x=\"").Append(F(x)).Append("\" y=\"106\" font-size=\"12\" ")
            .Append("fill=\"#94a3b8\">").Append(E(dump.Meta.CapturedAtUtc)).Append("</text>\n")
            .Append("<text x=\"").Append(F(x)).Append("\" y=\"146\" font-size=\"13\">")
            .Append("focus  ").Append(F(focus.X)).Append(", ").Append(F(focus.Y)).Append(", ")
            .Append(F(focus.Z)).Append("</text>\n")
            .Append("<text x=\"").Append(F(x)).Append("\" y=\"169\" font-size=\"13\">")
            .Append("view   ±").Append(F(radius)).Append(" m</text>\n")
            .Append("<text x=\"").Append(F(x)).Append("\" y=\"192\" font-size=\"13\">")
            .Append(ticks == 0 ? "state  captured" : $"state  +{ticks} ticks")
            .Append("</text>\n")
            .Append("<text x=\"").Append(F(x)).Append("\" y=\"215\" font-size=\"13\">")
            .Append("active ").Append(active).Append(" zombies</text>\n")
            .Append("<text x=\"").Append(F(x)).Append("\" y=\"238\" font-size=\"13\">")
            .Append("nav    ").Append(navFaces).Append(" faces (").Append(disabled)
            .Append(" disabled)</text>\n")
            .Append("<text x=\"").Append(F(x)).Append("\" y=\"261\" font-size=\"13\">")
            .Append("world  ").Append(collision).Append(" collision tris</text>\n")
            .Append("<text x=\"").Append(F(x)).Append("\" y=\"315\" font-size=\"14\" ")
            .Append("font-weight=\"800\">LEGEND</text>\n");
        Legend(svg, x, 347f, "#22c55e", "enabled navmesh");
        Legend(svg, x, 379f, "#fb923c", "disabled navmesh");
        Legend(svg, x, 411f, "#ef4444", "body-height collision");
        Legend(svg, x, 443f, "#2563eb", "player");
        Legend(svg, x, 475f, "#f59e0b", "zombie / route");
        svg.Append("<text x=\"").Append(F(x)).Append("\" y=\"535\" font-size=\"12\" ")
            .Append("fill=\"#94a3b8\">XZ top view · Z points up</text>\n")
            .Append("<text x=\"").Append(F(x)).Append("\" y=\"558\" font-size=\"12\" ")
            .Append("fill=\"#94a3b8\">SVG: zoom without losing detail</text>\n")
            .Append("</g>\n");
    }

    private static void Legend(StringBuilder svg, float x, float y, string colour, string label) =>
        svg.Append("<rect x=\"").Append(F(x)).Append("\" y=\"").Append(F(y - 13f))
            .Append("\" width=\"22\" height=\"14\" rx=\"3\" fill=\"").Append(colour)
            .Append("\"/><text x=\"").Append(F(x + 32f)).Append("\" y=\"").Append(F(y))
            .Append("\" font-size=\"13\">").Append(label).Append("</text>\n");

    private static void Polygon(StringBuilder svg, Vector3 a, Vector3 b, Vector3 c,
        Vector3 focus, float radius, float scale, string fill, string stroke,
        string opacity, string width) =>
        svg.Append("<polygon points=\"")
            .Append(F(X(a.X, focus, radius, scale))).Append(',').Append(F(Y(a.Z, focus, radius, scale))).Append(' ')
            .Append(F(X(b.X, focus, radius, scale))).Append(',').Append(F(Y(b.Z, focus, radius, scale))).Append(' ')
            .Append(F(X(c.X, focus, radius, scale))).Append(',').Append(F(Y(c.Z, focus, radius, scale)))
            .Append("\" fill=\"").Append(fill).Append("\" fill-opacity=\"").Append(opacity)
            .Append("\" stroke=\"").Append(stroke).Append("\" stroke-opacity=\"").Append(opacity)
            .Append("\" stroke-width=\"").Append(width).Append("\"/>\n");

    private static void Circle(StringBuilder svg, Vector3 point, float circleRadius, string fill,
        string stroke, Vector3 focus, float radius, float scale) =>
        svg.Append("<circle cx=\"").Append(F(X(point.X, focus, radius, scale)))
            .Append("\" cy=\"").Append(F(Y(point.Z, focus, radius, scale)))
            .Append("\" r=\"").Append(F(circleRadius)).Append("\" fill=\"").Append(fill)
            .Append("\" stroke=\"").Append(stroke).Append("\" stroke-width=\"2\"/>\n");

    private static bool TryVertex(IReadOnlyList<float> vertices, int index, out Vector3 vertex)
    {
        int at = index * 3;
        if (index < 0 || at + 2 >= vertices.Count)
        {
            vertex = default;
            return false;
        }
        vertex = new Vector3(vertices[at], vertices[at + 1], vertices[at + 2]);
        return true;
    }

    private static bool InView(Vector3 a, Vector3 b, Vector3 c, Vector3 focus, float radius) =>
        MathF.Max(a.X, MathF.Max(b.X, c.X)) >= focus.X - radius
        && MathF.Min(a.X, MathF.Min(b.X, c.X)) <= focus.X + radius
        && MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) >= focus.Z - radius
        && MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) <= focus.Z + radius;

    private static float X(float x, Vector3 focus, float radius, float scale) =>
        Margin + ((x - (focus.X - radius)) * scale);

    private static float Y(float z, Vector3 focus, float radius, float scale) =>
        Margin + (((focus.Z + radius) - z) * scale);

    private static string F(float value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string E(string value) => WebUtility.HtmlEncode(value);

    private sealed record Actor(int Id, EZombieState State, byte Target, Vector3 Position,
        IReadOnlyList<Vector3> Route);
}
