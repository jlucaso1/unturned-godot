using Godot;

namespace UnturnedGodot;

// A 1:1 port of Unturned's Skybox/Sky shader (Skybox-Sky.shader) as a Godot sky shader. Unity's rayDir is
// the INCOMING ray (-eyeRay), so rayDir = -EYEDIR and viewDir = EYEDIR; every dot product then matches the
// source verbatim. The pieces: a quartic gradient (equator held near the horizon, sky/ground past it), a
// smoothstepped sun disc, stars from a 2D texture projected onto an infinite plane and revealed by lowering
// _StarsCutoff at night, a moon rendered as an analytic sphere whose NdotL against the phase light
// direction draws the terminator (the phases), and three-scale scrolling clouds. Unity's _Time.x is t/20
// and _Time.y is t, hence the TIME factors. WITH_STARS / WITH_CLOUDS keywords become bool uniforms.
// Colors authored in sRGB carry the source_color hint (Unity converts color properties and sRGB textures
// to linear the same way); the clouds texture is authored linear (sRGBTexture: 0) and is sampled raw.
public static class UnturnedSky
{
    private static readonly Shader SkyShader = new()
    {
        Code = """
        shader_type sky;

        uniform vec3 sky_color : source_color;       // SKY_SKY
        uniform vec3 equator_color : source_color;   // SKY_EQUATOR
        uniform vec3 ground_color : source_color;    // SKY_GROUND
        uniform vec3 ambient_equator : source_color; // _SkyHackAmbientEquator
        uniform vec3 ambient_ground : source_color;  // _SkyHackAmbientGround
        uniform vec3 sun_direction = vec3(0.0, -1.0, 0.0); // light's travel direction (Unity sun.forward)
        uniform vec3 sun_color : source_color;
        uniform float sun_inner_threshold = 0.995;
        uniform float sun_outer_threshold = 0.993;
        uniform bool with_stars = false;             // WITH_STARS keyword
        uniform sampler2D stars_texture : source_color, repeat_enable, filter_linear_mipmap;
        uniform float stars_cutoff = 1.0;
        uniform vec3 moon_direction = vec3(0.0, 1.0, 0.0); // -sun.forward
        uniform vec3 moon_light_direction = vec3(0.0, 1.0, 0.0);
        uniform vec3 moon_color : source_color;
        uniform float sqr_moon_radius = 0.01;
        uniform bool with_clouds = false;            // WITH_CLOUDS keyword
        uniform sampler2D clouds_texture : repeat_enable, filter_linear_mipmap;
        uniform vec3 cloud_rim_color : source_color; // ELightingColor.CLOUDS
        uniform float cloud_intensity = 0.0;         // ELightingSingle.CLOUDS
        uniform vec2 cloud_params = vec2(0.6, 10.0); // R: macro alpha cutoff, G: contrast saturation
        uniform float atmospheric_fog = 0.0;         // weather's _AtmosphericFog global (clear sky = 0)
        uniform vec3 fog_color : source_color;

        vec3 blend_clouds(vec3 col, vec3 view_dir) {
            // Particle clouds traveled along world +Z, which is -Y in UV space (source comment).
            vec2 texcoord = view_dir.xz / view_dir.y;
            float t20 = TIME / 20.0; // Unity _Time.x

            float macro_alpha = texture(clouds_texture, texcoord * 0.1 - vec2(0.0, t20 * 0.01)).r;
            macro_alpha += cloud_intensity * 0.25
                * texture(clouds_texture, texcoord * 0.1 + 0.5 - vec2(0.0, t20 * 0.01)).r;
            macro_alpha = clamp((macro_alpha - cloud_params.r) * cloud_params.g, 0.0, 1.0);

            float sun_atmosphere = clamp(sun_direction.y * -2.0 + 1.0, 0.0, 1.0);
            float sun_view = clamp(0.5 - dot(view_dir, sun_direction), 0.0, 1.0);
            float sun_factor = sun_atmosphere * sun_view;

            float moon_atmosphere = clamp(moon_direction.y * -2.0 + 1.0, 0.0, 1.0);
            float moon_view = clamp(-dot(view_dir, moon_direction), 0.0, 1.0);
            float moon_factor = moon_atmosphere * moon_view;

            float clouds_medium = texture(clouds_texture, texcoord * 0.2 - vec2(0.0, t20 * 0.04)).g;
            float clouds_small = texture(clouds_texture, texcoord - vec2(0.0, t20 * 0.2)).b;

            vec3 body = ambient_ground + cloud_rim_color;
            body = mix(body, sun_color, sun_factor * clouds_medium * 0.5);
            body = mix(body, moon_color, moon_factor * clouds_medium * 0.05);

            vec3 rim = ambient_equator + equator_color + cloud_rim_color;
            rim = mix(rim, sun_color, sun_factor);
            rim = mix(rim, moon_color, moon_factor * 0.25);

            float body_alpha = clamp(macro_alpha + macro_alpha * clouds_medium + macro_alpha * clouds_small, 0.0, 1.0);
            float clouds_alpha = body_alpha * clamp(view_dir.y * 2.0, 0.0, 1.0); // fade toward the horizon
            return mix(col, mix(rim, body, body_alpha), clouds_alpha);
        }

        void sky() {
            vec3 ray_dir = -EYEDIR;  // Unity rayDir: the incoming ray
            vec3 view_dir = EYEDIR;

            // Quartic gradient: equator hugs the horizon, sky above, ground below (hard cutoff at y=0).
            float scale = 1.0 - pow(1.0 - clamp(abs(ray_dir.y), 0.0, 1.0), 4.0);
            vec3 col;
            float over_horizon_mask;
            if (ray_dir.y < 0.0) {
                col = mix(equator_color, sky_color, scale);
                over_horizon_mask = 1.0;
            } else {
                col = mix(equator_color, ground_color, scale);
                over_horizon_mask = 0.0;
            }

            float sun_alignment = dot(ray_dir, sun_direction);
            float sun_alpha = smoothstep(sun_outer_threshold, sun_inner_threshold, sun_alignment);
            sun_alpha *= over_horizon_mask;
            float sun_intensity = mix(4.0, 1.0, atmospheric_fog);

            // Moon: analytic ray/sphere hit, lit by the phase light direction (the terminator).
            vec3 moon_center = -moon_direction;
            float moon_dist_along_view = dot(view_dir, moon_center);
            float moon_mask = (moon_dist_along_view > 0.0 ? 1.0 : 0.0) * over_horizon_mask;
            float sqr_dist_to_ray = 1.0 - moon_dist_along_view * moon_dist_along_view;
            moon_mask *= sqr_dist_to_ray < sqr_moon_radius ? 1.0 : 0.0;
            float dist_within_moon = sqrt(max(sqr_moon_radius - sqr_dist_to_ray, 0.0));
            vec3 moon_hit = view_dir * (moon_dist_along_view - dist_within_moon);
            vec3 moon_hit_normal = normalize(moon_hit - moon_center);
            float moon_ndotl = clamp(dot(moon_hit_normal, -moon_light_direction), 0.0, 1.0);

            if (with_stars) {
                // Infinite-plane projection with a slow drift; the moon occludes the stars behind it.
                vec2 stars_coord = ray_dir.xz / ray_dir.y;
                stars_coord.x += TIME / 20.0 * 0.01; // Unity _Time.x * 0.01
                stars_coord.y += TIME * 0.004;       // Unity _Time.y * 0.004
                vec4 stars = texture(stars_texture, stars_coord * 0.6);
                float stars_mask = clamp(-ray_dir.y, 0.0, 1.0) * (1.0 - moon_mask);
                col = mix(col, stars.rgb, max(0.0, stars.a - stars_cutoff) * stars_mask);
            }

            col = mix(col, sun_color * sun_intensity, sun_alpha);
            col = mix(col, moon_color, moon_mask * moon_ndotl);

            if (with_clouds) {
                col = blend_clouds(col, view_dir);
            }

            col = mix(col, fog_color, atmospheric_fog);
            COLOR = col;
        }
        """,
    };

    public static ShaderMaterial Build(SkyboxAssets? assets)
    {
        var material = new ShaderMaterial { Shader = SkyShader };
        if (assets == null)
            return material; // plain gradient + sun disc with the shader's authored defaults

        material.SetShaderParameter("sun_inner_threshold", assets.SunInnerThreshold);
        material.SetShaderParameter("sun_outer_threshold", assets.SunOuterThreshold);
        material.SetShaderParameter("sqr_moon_radius", assets.SqrMoonRadius);
        material.SetShaderParameter("moon_color", assets.MoonColor);
        material.SetShaderParameter("cloud_params", new Vector2(assets.CloudParams.R, assets.CloudParams.G));
        if (assets.Stars != null)
        {
            material.SetShaderParameter("with_stars", true);
            material.SetShaderParameter("stars_texture", assets.Stars);
        }
        if (assets.Clouds != null)
        {
            material.SetShaderParameter("with_clouds", true);
            material.SetShaderParameter("clouds_texture", assets.Clouds);
        }
        return material;
    }
}
