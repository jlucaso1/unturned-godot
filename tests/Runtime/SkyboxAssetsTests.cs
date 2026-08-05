using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Assets;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The game's real skybox data: the Level/Skybox material's authored scalars, plus the stars and clouds
// textures it references out of resources.assets.
//
// This is the input UnturnedSky.Build takes, and the shader is a 1:1 port — so the scalars are not
// tuning knobs to be guessed at, they are the values the game itself renders with. Reading them wrong
// gives a sky that is subtly the wrong shape rather than one that is obviously broken.
//
// It needs the install: resources.assets ships with its type trees stripped, so this borrows the
// masterbundle's, and neither file has a synthetic stand-in worth writing. Without the game it says so
// and returns; UG_REQUIRE_REAL_DATA=1 turns that into a failure.
public class SkyboxAssetsTests : TestClass
{
    public SkyboxAssetsTests(Node testScene) : base(testScene) { }

    // A path with no game under it is null rather than a throw: the sky falls back to the shader's own
    // authored defaults, which is what a map loaded before extraction finishes already does.
    [Test]
    public void NoInstallMeansNoAssetsRatherThanAThrow()
    {
        Assert.Null(SkyboxAssets.Load(Path.Combine(Path.GetTempPath(), "unturned-godot-no-install")));
    }

    // The real thing. The scalars have to come back in ranges the shader can use — the sun thresholds are
    // cosines of very small angles, so they sit just under 1, and a moon radius is a squared fraction.
    // Values outside those are not "different tuning", they are a misread field.
    [Test]
    public void TheRealSkyboxScalarsAreInTheShadersRanges()
    {
        string? install = UnturnedInstall.Find();
        if (!Available("an Unturned install", install))
            return;

        SkyboxAssets? assets = SkyboxAssets.Load(install!);
        if (assets == null)
        {
            // A platform whose resources.assets this reader does not decode is a real answer, not a
            // failure: the sky falls back to its defaults and the map still loads.
            Log.Print("[runtime-tests] the install carries no readable skybox material");
            return;
        }

        Assert.True(assets.SunInnerThreshold is > 0.9f and <= 1f,
            $"sun inner threshold {assets.SunInnerThreshold} is not a cosine of a small angle");
        Assert.True(assets.SunOuterThreshold is > 0.9f and <= 1f,
            $"sun outer threshold {assets.SunOuterThreshold} is not a cosine of a small angle");
        // The outer edge of the disc is a wider angle than the inner one, so its cosine is smaller. The
        // two swapped would invert the falloff and give the sun a hard rim.
        Assert.True(assets.SunOuterThreshold <= assets.SunInnerThreshold,
            "the sun's outer threshold is inside its inner one");
        Assert.True(assets.SqrMoonRadius is > 0f and < 0.1f,
            $"squared moon radius {assets.SqrMoonRadius} is out of range");
    }

    // And what it produces has to be usable by the sky it feeds. This is the assertion that matters: the
    // two halves are only correct together.
    [Test]
    public void WhatItReadsIsWhatTheSkyCanBuildFrom()
    {
        string? install = UnturnedInstall.Find();
        if (!Available("an Unturned install", install))
            return;

        SkyboxAssets? assets = SkyboxAssets.Load(install!);
        if (assets == null)
            return;

        ShaderMaterial sky = UnturnedSky.Build(assets);

        Assert.NotNull(sky.Shader);
        Assert.Equal(assets.SunInnerThreshold, (float)sky.GetShaderParameter("sun_inner_threshold"));
        Assert.Equal(assets.SqrMoonRadius, (float)sky.GetShaderParameter("sqr_moon_radius"));
        // Whichever textures came out of the install are switched on, and whichever did not are off —
        // extraction can land one before the other.
        Assert.Equal(assets.Stars != null, (bool)sky.GetShaderParameter("with_stars"));
        Assert.Equal(assets.Clouds != null, (bool)sky.GetShaderParameter("with_clouds"));
    }

    private static bool Available(string what, string? path)
    {
        if (path != null)
            return true;
        if (System.Environment.GetEnvironmentVariable("UG_REQUIRE_REAL_DATA") == "1")
        {
            throw new InvalidDataException(
                $"UG_REQUIRE_REAL_DATA=1 but {what} is not present; this run exists to prove these tests execute");
        }

        Log.Print($"[runtime-tests] skipping: {what} is not present "
            + "(set UNTURNED_PATH or run ./scripts/fetch-game-data.sh)");
        return false;
    }
}
