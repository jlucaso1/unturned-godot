using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.UI;

namespace UnturnedGodot;

// The crosshair and the hitmarkers — PlayerLifeUI's Crosshair and SleekHitmarker, in the two states a
// bare-handed player is ever in.
//
// The crosshair is the CENTRE DOT alone, and that is the whole of it rather than a simplification: the
// directional arrows belong to UseableGun, which sizes them from the weapon's spread every frame, and
// PlayerMovement.enableDot is what turns the dot on for a player who is not holding one.
//
// The marks are pooled, because a mark lives a third of a second and a burst of hits overlaps several.
public sealed partial class PlayerHud : CanvasLayer
{
    // How many marks can be on screen at once. HIT_TIME is 0.33 s and a fist swings every 0.48 s, so one
    // is enough for punching alone — the rest are headroom for a weapon that fires faster, and for the
    // hits of other things arriving at once.
    public const int MaxMarks = 8;

    private sealed class Mark
    {
        public required TextureRect[] Wedges;
        public double Elapsed;
        public float Jitter;
        public bool Active;
    }

    private readonly HudIconSet _icons;
    private readonly RandomNumberGenerator _rng = new();
    private readonly List<Mark> _marks = new();
    private TextureRect? _dot;
    private Control? _root;

    // OptionsSettings.hitmarkerStyle. Animated is the shipped default.
    public EHitmarkerStyle Style { get; set; } = EHitmarkerStyle.Animated;

    // Whether the crosshair dot is drawn. PlayerMovement turns it off in a vehicle and UseableGun turns
    // it off for a gun; nothing here does either yet, so it follows the player being alive and on foot.
    public bool DotVisible
    {
        get => _dot?.Visible ?? false;
        set { if (_dot != null) _dot.Visible = value; }
    }

    public PlayerHud(HudIconSet icons)
    {
        _icons = icons ?? throw new ArgumentNullException(nameof(icons));
        Name = "PlayerHud";
        // Above the world, below the menus, which claim their own layers.
        Layer = 1;
    }

    public override void _Ready()
    {
        _root = new Control
        {
            Name = "Hud",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        // Crosshair.centerDotImage: 8 px, centred, tinted and half transparent.
        _dot = NewIcon(HudIcons.Dot, 8);
        if (_dot != null)
        {
            _dot.Modulate = Hitmarker.CrosshairColor;
            _dot.AnchorLeft = _dot.AnchorRight = _dot.AnchorTop = _dot.AnchorBottom = 0.5f;
            _dot.OffsetLeft = _dot.OffsetTop = -4;
            _dot.OffsetRight = _dot.OffsetBottom = 4;
            _root.AddChild(_dot);
        }
    }

    // Flashes a mark. Silently does nothing for EPlayerHit.None, which is what a swing that found nothing
    // reports — PlayerUI.hitmark is simply never called in that case.
    public void Hitmark(EPlayerHit hit)
    {
        if (hit == EPlayerHit.None || _root == null)
            return;

        string file = Hitmarker.UsesEntityIcon(hit) ? HudIcons.HitEntity
            : hit == EPlayerHit.Ghost ? HudIcons.HitGhost
            : HudIcons.HitBuild;
        Texture2D? texture = _icons.Get(file);
        if (texture == null)
            return; // no icon extracted: no mark, rather than a placeholder

        Mark mark = Claim();
        mark.Elapsed = 0;
        mark.Jitter = _rng.RandfRange(-Hitmarker.MaxAngleJitterDegrees,
            Hitmarker.MaxAngleJitterDegrees);
        mark.Active = true;
        Color color = Hitmarker.ColorFor(hit);
        foreach (TextureRect wedge in mark.Wedges)
        {
            wedge.Texture = texture;
            wedge.Modulate = color;
            wedge.Visible = true;
        }

        Layout(mark);
    }

    public override void _Process(double delta)
    {
        for (int i = 0; i < _marks.Count; i++)
        {
            Mark mark = _marks[i];
            if (!mark.Active)
                continue;
            mark.Elapsed += delta;
            if (mark.Elapsed >= Hitmarker.LifetimeSeconds)
            {
                mark.Active = false;
                foreach (TextureRect wedge in mark.Wedges)
                    wedge.Visible = false;
                continue;
            }

            if (Style == EHitmarkerStyle.Animated)
                Layout(mark);
        }
    }

    private void Layout(Mark mark)
    {
        Span<Vector2> positions = stackalloc Vector2[4];
        Span<float> rotations = stackalloc float[4];

        if (Style == EHitmarkerStyle.Animated)
        {
            Hitmarker.Animated((float)(mark.Elapsed / Hitmarker.LifetimeSeconds), mark.Jitter,
                positions, rotations);
            for (int i = 0; i < 4; i++)
            {
                TextureRect wedge = mark.Wedges[i];
                // Screen fractions become anchors, so the mark tracks the window rather than a fixed
                // resolution — the original places them the same way (PositionScale).
                wedge.AnchorLeft = wedge.AnchorRight = positions[i].X;
                wedge.AnchorTop = wedge.AnchorBottom = positions[i].Y;
                wedge.OffsetLeft = wedge.OffsetTop = -Hitmarker.HalfImageSize;
                wedge.OffsetRight = wedge.OffsetBottom = Hitmarker.HalfImageSize;
                wedge.RotationDegrees = rotations[i];
            }

            return;
        }

        Hitmarker.Classic(positions, rotations);
        for (int i = 0; i < 4; i++)
        {
            TextureRect wedge = mark.Wedges[i];
            wedge.AnchorLeft = wedge.AnchorRight = wedge.AnchorTop = wedge.AnchorBottom = 0.5f;
            wedge.OffsetLeft = positions[i].X - Hitmarker.HalfImageSize;
            wedge.OffsetTop = positions[i].Y - Hitmarker.HalfImageSize;
            wedge.OffsetRight = positions[i].X + Hitmarker.HalfImageSize;
            wedge.OffsetBottom = positions[i].Y + Hitmarker.HalfImageSize;
            wedge.RotationDegrees = rotations[i];
        }
    }

    // The next mark to use: a free one, else a fresh one, else the one that has been up longest.
    private Mark Claim()
    {
        foreach (Mark existing in _marks)
            if (!existing.Active)
                return existing;

        if (_marks.Count < MaxMarks)
        {
            var wedges = new TextureRect[4];
            for (int i = 0; i < 4; i++)
            {
                TextureRect wedge = NewIcon(HudIcons.HitEntity, Hitmarker.ImageSize)
                    ?? new TextureRect { Visible = false };
                wedge.PivotOffset = new Vector2(Hitmarker.HalfImageSize, Hitmarker.HalfImageSize);
                wedge.Visible = false;
                _root!.AddChild(wedge);
                wedges[i] = wedge;
            }

            var created = new Mark { Wedges = wedges };
            _marks.Add(created);
            return created;
        }

        Mark oldest = _marks[0];
        foreach (Mark candidate in _marks)
            if (candidate.Elapsed > oldest.Elapsed)
                oldest = candidate;
        return oldest;
    }

    // A TextureRect for one icon, or null when the icon was never extracted. Sized in pixels, ignoring
    // the mouse, and stretched to fill — these are tiny sprites drawn at their authored size.
    private TextureRect? NewIcon(string file, int size)
    {
        Texture2D? texture = _icons.Get(file);
        if (texture == null)
            return null;

        return new TextureRect
        {
            Texture = texture,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            CustomMinimumSize = new Vector2(size, size),
        };
    }
}
