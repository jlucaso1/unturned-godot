using Godot;

namespace UnturnedGodot;

// Unturned's sun shafts (LevelLighting.UpdateSunShafts): the streaks that fan out of the sun when you
// look at it. The original is Unity's Standard Assets SunShafts image effect — a radial blur of the sky
// away from the sun's screen position, added back over the frame — driven by the map's own lighting data:
//
//     raysColor     = colors[ELightingColor.RAYS]
//     raysIntensity = singles[ELightingSingle.RAYS] * 4
//     UpdateSunShaftsIntensity(1 - fog)
//
// Both come straight from Lighting.dat per keyframe, so this reads them off the cycle rather than
// carrying any constants of its own.
//
// The effect is a screen-space quad parented to the camera and drawn after everything else. Godot has no
// canvas-item access to the depth buffer, so a spatial shader on a quad is what can mask the blur to the
// sky — geometry must occlude the shafts, or the streaks paint straight over buildings.
//
// Unturned ships this OFF by default (GraphicsSettingsData.SunShaftsQuality = OFF), so it is opt-in here
// too; SUN_SHAFTS=1 turns it on.
public partial class SunShafts : Node3D
{
    // Unity's SunShafts constants, kept as its own fields rather than the map's data: the map controls
    // colour and strength, the effect controls its shape.
    private const int Samples = 24;      // taps along the ray toward the sun
    private const float Density = 0.65f; // how far along the screen the streaks reach
    private const float Decay = 0.94f;   // per-tap falloff, so the streaks fade outward

    // Unity's SunShafts fades the source radially away from the sun (_SunPosition.w, "max radius"), so
    // bright sky far from it throws nothing. Without this every lit cloud in frame feeds the blur and
    // the streaks pick up whatever happens to be bright somewhere else.
    private const float MaxRadius = 0.75f;

    // Unity blurs on a downsampled buffer (sunShaftsResolution) across three iterations, which is what
    // makes its shafts soft. One full-resolution pass cannot match that, but sampling a lower mip of the
    // screen blurs the source the same way — without it a sun peeking round a cloud is a hard-edged fan
    // of streaks rather than a glow.
    //
    // Cheap, not free: `filter_linear_mipmap` on a hint_screen_texture is what makes Godot build the
    // screen's mip chain ("Godot will automatically calculate the blurred texture for you"), and it does
    // that every frame this pass draws. Blurring by hand instead costs far MORE, not less. The loop below
    // takes one screen tap per sample, so replacing each with a 4-tap box on mip 0 adds 3 extra taps x
    // Samples x every pixel on screen — ~100M taps at 1600x900, against the ~0.5M pixels a mip chain
    // writes (the levels below the first sum to about a third of the frame). Keep the mip.
    private const float SourceMip = 1.0f;

    // Unity's SunShafts subtracts a threshold before blurring, so only what is BRIGHTER than the sky
    // throws shafts. Without it the whole sky qualifies, every tap contributes, and the frame washes
    // out to flat colour instead of showing streaks. These are the Standard Assets defaults.
    private const float ThresholdR = 0.87f;
    private const float ThresholdG = 0.74f;
    private const float ThresholdB = 0.65f;

    private ShaderMaterial _material = null!;
    private MeshInstance3D _quad = null!;
    private Camera3D? _camera;
    private DirectionalLight3D _sun = null!;

    // Read once when the pass is built, not once per rendered frame.
    //
    // `OS.GetEnvironment` is a marshalled call that hands back a Godot String and converts it to a
    // managed one, and _Process was making it every frame to decide whether to log a diagnostic that is
    // off — the only per-frame GetEnvironment left in src/, out of 137 call sites. Every other flag in
    // the project is read once and kept (TextureRegistry, ModelLibrary, FoliageBuilder all do this).
    // Kept on the instance rather than in a static initializer for one reason: a SunShafts is built once
    // per world, so the saving is the same, and the runtime test that walks all three debug branches can
    // still set the variable around the build instead of needing it set before the assembly loads.
    private bool _debug;

    // Everything is built here rather than in _Ready: the day/night cycle pushes its first Configure
    // while wiring the world up, before this node has entered the tree, and a half-built pass would
    // throw straight through the caller.
    public static SunShafts Create(DirectionalLight3D sun)
    {
        var shafts = new SunShafts { Name = "SunShafts", _sun = sun };
        shafts._debug = EnvFlag.IsOn(OS.GetEnvironment("SUN_SHAFTS_DEBUG"), whenUnset: false);
        shafts._material = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };
        shafts._quad = new MeshInstance3D
        {
            Name = "SunShaftsQuad",
            // The shader samples in screen space and ignores the geometry, so the mesh only has to be
            // guaranteed on screen — it is sized and placed against the near plane in Follow.
            Mesh = new QuadMesh { Size = new Vector2(2f, 2f), FlipFaces = true },
            MaterialOverride = shafts._material,
            // Never culled away and never shadow-casting: it is a full-screen pass, not world geometry.
            ExtraCullMargin = 16384f,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false, // until a camera is attached and the sun is actually on screen
        };
        shafts.AddChild(shafts._quad);
        return shafts;
    }

    // The camera to hang the pass off. Set by whoever spawns the player or the free camera.
    public void Follow(Camera3D camera)
    {
        _camera = camera;
        if (_quad.GetParent() != null)
            _quad.GetParent().RemoveChild(_quad);
        camera.AddChild(_quad);
        // Just inside the near plane, so the quad is always in front of the camera and always drawn.
        _quad.Position = new Vector3(0f, 0f, -(camera.Near + 0.02f));
    }

    // Per-frame state from the day/night cycle: the map's rays colour and the RAYS single (already
    // multiplied by 4, as LevelLighting does), plus the fog fade UpdateSunShaftsIntensity applies.
    public void Configure(Color raysColor, float raysIntensity, float fogFade)
    {
        _material.SetShaderParameter("rays_color", raysColor);
        _material.SetShaderParameter("intensity", raysIntensity * fogFade);
    }

    public override void _Process(double delta)
    {
        if (_camera == null || !IsInstanceValid(_camera))
        {
            if (_debug)
                Log.Print("[shafts] no camera attached");
            _quad.Visible = false;
            return;
        }

        // The sun is directional, so its "position" is a point far along the direction the light comes
        // FROM — that is +Z of the light's basis, since a DirectionalLight3D shines down its -Z.
        Vector3 sunWorld = _camera.GlobalPosition + (_sun.GlobalTransform.Basis.Z * 4096f);
        if (_camera.IsPositionBehind(sunWorld))
        {
            if (_debug)
            {
                // Report where it actually is, so a screenshot can be aimed at it.
                Vector3 d = (sunWorld - _camera.GlobalPosition).Normalized();
                Log.Print($"[shafts] sun is behind the camera; it sits at yaw " +
                    $"{Mathf.RadToDeg(Mathf.Atan2(-d.X, -d.Z)):0.#} pitch " +
                    $"{Mathf.RadToDeg(Mathf.Asin(d.Y)):0.#}");
            }
            _quad.Visible = false; // sun off screen behind us: nothing to streak from
            return;
        }

        Vector2 screen = _camera.UnprojectPosition(sunWorld);
        Vector2 size = GetViewport().GetVisibleRect().Size;
        if (size.X <= 0f || size.Y <= 0f)
        {
            _quad.Visible = false;
            return;
        }

        _quad.Visible = true;
        _material.SetShaderParameter("sun_screen", screen / size);
        if (_debug)
            Log.Print($"[shafts] sun uv=({screen.X / size.X:0.###},{screen.Y / size.Y:0.###}) " +
                $"intensity={_material.GetShaderParameter("intensity")} visible={_quad.Visible}");
    }

    // Ported from Unity's SunShafts: walk from the pixel toward the sun's screen position, accumulating
    // only what the depth buffer says is sky, with a geometric falloff per step. Godot's depth buffer is
    // reversed (0 at the far plane), so "sky" is depth at (or extremely near) zero.
    private static readonly string ShaderCode = $$"""
    #pragma disable_preprocessor
    shader_type spatial;
    render_mode unshaded, blend_add, depth_test_disabled, depth_draw_never, cull_disabled, fog_disabled;

    uniform vec2 sun_screen = vec2(0.5);
    uniform vec4 rays_color : source_color = vec4(1.0);
    uniform float intensity = 0.0;

    uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;
    uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;

    void fragment() {
        if (intensity <= 0.0) {
            ALBEDO = vec3(0.0);
        } else {
            vec2 toward = (SCREEN_UV - sun_screen) * ({{Density}} / float({{Samples}}));
            // Offset the first tap by a per-pixel dither. Every pixel marching the same fixed steps is
            // what banded the streaks into visible spokes; breaking the phase turns the banding into
            // noise the eye reads as a smooth gradient.
            float jitter = fract(sin(dot(SCREEN_UV, vec2(12.9898, 78.233))) * 43758.5453);
            vec2 uv = SCREEN_UV - toward * jitter;
            float illumination = 1.0;
            float total = 0.0;
            vec3 accumulated = vec3(0.0);
            const vec3 threshold = vec3({{ThresholdR}}, {{ThresholdG}}, {{ThresholdB}});

            for (int i = 0; i < {{Samples}}; i++) {
                uv -= toward;
                vec2 clamped = clamp(uv, vec2(0.0), vec2(1.0));
                // Sky only. Anything with geometry in front of it contributes nothing, which is what
                // makes the shafts break against buildings and trees instead of drawing over them.
                float depth = texture(depth_tex, clamped).r;
                float sky = step(depth, 0.000001);
                // Radial falloff from the sun, then the brightness threshold: only what is near the sun
                // AND brighter than the sky throws a shaft.
                float radial = clamp({{MaxRadius}} - length(clamped - sun_screen), 0.0, 1.0);
                vec3 source = textureLod(screen_tex, clamped, {{SourceMip}}).rgb;
                vec3 bright = max(source - threshold, vec3(0.0));
                accumulated += bright * sky * radial * illumination;
                total += illumination;
                illumination *= {{Decay}};
            }

            // Normalised by the weights actually applied, so the tap count and decay set the SHAPE of
            // the streaks while the map's RAYS value alone sets how strong they are.
            ALBEDO = (accumulated / max(total, 0.001)) * rays_color.rgb * intensity;
        }
    }
    """;
}
