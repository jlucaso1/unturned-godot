using System.IO;
using System.Text;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The day/night cycle and the two engine-side pieces it drives: the ported skybox shader and the sun-shaft
// pass. LightingCycle — the part that turns a time of day into a lighting state — is pure and already unit
// tested in core/. What is only testable here is what the controller DOES with that state: a
// DirectionalLight3D, a WorldEnvironment, a ShaderMaterial and a full-screen quad.
//
// The lighting data is synthetic, built to the real Lighting.dat layout, so none of this needs the game
// installed. SkyboxAssets is deliberately not covered here: it reads resources.assets out of a Steam
// install, which a hermetic runner does not have.
public class EnvironmentTests : TestClass
{
    public EnvironmentTests(Node testScene) : base(testScene) { }

    // The sky is a ported shader, and it must build even with no extracted assets: a map loaded before
    // the skybox extraction finishes still needs a sky, and a null material would be a black screen.
    [Test]
    public void TheSkyBuildsWithoutExtractedAssets()
    {
        ShaderMaterial sky = UnturnedSky.Build(null);

        Assert.NotNull(sky);
        Assert.NotNull(sky.Shader);
    }

    // With the real skybox extracted, the authored scalars and textures reach the shader. The two
    // textures are separately optional — extraction can land the stars before the clouds — so each gets
    // its own flag rather than one "assets are ready" switch.
    [Test]
    public void ExtractedSkyboxAssetsReachTheShader()
    {
        var assets = new SkyboxAssets
        {
            SunInnerThreshold = 0.9993f,
            SunOuterThreshold = 0.9988f,
            SqrMoonRadius = 0.00035f,
            MoonColor = new Color(0.8f, 0.85f, 1f),
            CloudParams = new Color(0.35f, 2.5f, 0f),
            Stars = Pixel(),
            Clouds = Pixel(),
        };

        ShaderMaterial sky = UnturnedSky.Build(assets);

        Assert.Equal(0.9993f, (float)sky.GetShaderParameter("sun_inner_threshold"));
        Assert.Equal(0.9988f, (float)sky.GetShaderParameter("sun_outer_threshold"));
        Assert.Equal(0.00035f, (float)sky.GetShaderParameter("sqr_moon_radius"));
        Assert.Equal(new Color(0.8f, 0.85f, 1f), (Color)sky.GetShaderParameter("moon_color"));
        // R and G only: the shader wants the macro alpha cutoff and the contrast saturation.
        Assert.Equal(new Vector2(0.35f, 2.5f), (Vector2)sky.GetShaderParameter("cloud_params"));
        Assert.True((bool)sky.GetShaderParameter("with_stars"));
        Assert.True((bool)sky.GetShaderParameter("with_clouds"));
    }

    // Half-extracted assets: the scalars still apply, and the texture that has not landed leaves its own
    // feature switched off instead of binding a null sampler.
    [Test]
    public void AHalfExtractedSkyboxLeavesTheMissingFeatureOff()
    {
        ShaderMaterial sky = UnturnedSky.Build(new SkyboxAssets
        {
            SunInnerThreshold = 0.5f,
            SunOuterThreshold = 0.4f,
            SqrMoonRadius = 0.001f,
            MoonColor = Colors.White,
            CloudParams = new Color(0.1f, 1f, 0f),
            Stars = Pixel(),
            Clouds = null, // still being extracted
        });

        Assert.True((bool)sky.GetShaderParameter("with_stars"));
        Assert.False((bool)sky.GetShaderParameter("with_clouds"));
    }

    // Everything the cycle drives is built up front and wired together, so no frame has to check whether
    // the sun exists before it can be pointed somewhere.
    [Test]
    public void BuildingTheCycleCreatesTheSunAndTheEnvironment()
    {
        DayNightController cycle = DayNightController.Build(Lighting(), waterMaterial: null);
        TestScene.AddChild(cycle);

        Assert.NotNull(cycle.GetNodeOrNull<DirectionalLight3D>("Sun"));
        WorldEnvironment? env = FindEnvironment(cycle);
        Assert.NotNull(env);
        Assert.NotNull(env!.Environment.Sky);

        cycle.QueueFree();
    }

    // Time of day wraps rather than clamping: a repro dump restores whatever the session was at, the
    // cycle runs past 1 every day, and "it only happens at night" is a real bug report.
    [Test]
    public void TimeOfDayWrapsIntoRange()
    {
        DayNightController cycle = DayNightController.Build(Lighting(), waterMaterial: null);

        cycle.TimeOfDay = 1.25f;
        Assert.True(Mathf.Abs(cycle.TimeOfDay - 0.25f) < 0.0001f, $"got {cycle.TimeOfDay}");

        cycle.TimeOfDay = -0.25f; // and backwards, which PosMod handles and a clamp would not
        Assert.True(Mathf.Abs(cycle.TimeOfDay - 0.75f) < 0.0001f, $"got {cycle.TimeOfDay}");

        cycle.Free();
    }

    // The whole point of the controller: the frame's lighting comes from the map's own keyframes, so
    // midday and midnight must not look the same. Asserted on the sun, which is what a player sees.
    [Test]
    public async Task MiddayAndMidnightLightTheWorldDifferently()
    {
        DayNightController cycle = DayNightController.Build(Lighting(), waterMaterial: null);
        TestScene.AddChild(cycle);
        var sun = cycle.GetNode<DirectionalLight3D>("Sun");

        cycle.TimeOfDay = 0.5f; // midday
        await NextFrame();
        (Color middayColor, float middayEnergy, Vector3 middayAngle) =
            (sun.LightColor, sun.LightEnergy, sun.RotationDegrees);

        cycle.TimeOfDay = 0f; // midnight
        await NextFrame();

        Assert.True(middayColor != sun.LightColor || !Mathf.IsEqualApprox(middayEnergy, sun.LightEnergy),
            "the sun looked identical at midday and midnight");
        Assert.True(middayAngle.DistanceTo(sun.RotationDegrees) > 1f,
            $"the sun did not move across the sky: {middayAngle} vs {sun.RotationDegrees}");

        cycle.QueueFree();
    }

    // TIME_OF_DAY is what the screenshot and benchmark paths rely on: it pins the light so two captures
    // of the same scene are comparable rather than drifting apart by however long each took to reach the
    // grab. It both sets the time AND freezes the clock, and the freeze is the half that matters.
    [Test]
    public async Task TimeOfDayEnvironmentVariablePinsAndFreezesTheCycle()
    {
        DayNightController cycle = await WithEnvironment("TIME_OF_DAY", "0.42", async () =>
        {
            DayNightController built = DayNightController.Build(Lighting(), waterMaterial: null);
            TestScene.AddChild(built);
            for (int i = 0; i < 5; i++)
                await NextFrame();
            return built;
        });

        Assert.True(Mathf.Abs(cycle.TimeOfDay - 0.42f) < 0.0001f,
            $"TIME_OF_DAY=0.42 ended at {cycle.TimeOfDay}");

        cycle.QueueFree();
    }

    // The other two knobs the same block reads. MOON_PHASE wraps rather than throwing on a number past
    // the cycle count, and DAY_SPEED scales the clock — a speed of zero is another way to hold still.
    [Test]
    public async Task MoonPhaseAndDaySpeedAreReadFromTheEnvironment()
    {
        DayNightController fast = await WithEnvironment("DAY_SPEED", "5000", async () =>
        {
            DayNightController built = DayNightController.Build(Lighting(), waterMaterial: null);
            TestScene.AddChild(built);
            built.TimeOfDay = 0f;
            for (int i = 0; i < 3; i++)
                await NextFrame();
            return built;
        });
        Assert.True(fast.TimeOfDay > 0f, "DAY_SPEED did not make the clock run");
        fast.QueueFree();

        // Past the phase count on purpose: it wraps, so a stale value in a shell cannot crash a load.
        DayNightController moon = await WithEnvironment("MOON_PHASE", "99", async () =>
        {
            DayNightController built = DayNightController.Build(Lighting(), waterMaterial: null);
            TestScene.AddChild(built);
            await NextFrame();
            return built;
        });
        moon.QueueFree();
    }

    // A running cycle does advance — the other half of the freeze test, and the thing that would make a
    // frozen-looking bug invisible if it were broken the other way.
    [Test]
    public async Task ARunningCycleAdvances()
    {
        DayNightController cycle = DayNightController.Build(Lighting(), waterMaterial: null);
        TestScene.AddChild(cycle);

        cycle.TimeOfDay = 0.42f;
        for (int i = 0; i < 5; i++)
            await NextFrame();

        Assert.True(Mathf.Abs(cycle.TimeOfDay - 0.42f) > 0f, "the cycle never advanced");
        cycle.QueueFree();
    }

    // The sea plane is tinted from the same keyframes as everything else, so water at dusk is not the
    // colour it was at noon.
    [Test]
    public async Task TheWaterIsTintedFromTheSameKeyframes()
    {
        var water = new StandardMaterial3D();
        DayNightController cycle = DayNightController.Build(Lighting(), water);
        TestScene.AddChild(cycle);

        cycle.TimeOfDay = 0.5f;
        await NextFrame();
        Color midday = water.AlbedoColor;

        cycle.TimeOfDay = 0f;
        await NextFrame();

        Assert.True(midday != water.AlbedoColor, $"the sea stayed {midday} from midday to midnight");
        cycle.QueueFree();
    }

    // The shafts follow the camera they render in front of. Handing over a camera is how the effect gets
    // its screen position for the sun, and doing it twice (player, then free camera) must not break it.
    [Test]
    public void TheSunShaftsCanBeGivenACameraMoreThanOnce()
    {
        DayNightController cycle = DayNightController.Build(Lighting(), waterMaterial: null);
        TestScene.AddChild(cycle);

        var first = new Camera3D();
        var second = new Camera3D();
        TestScene.AddChild(first);
        TestScene.AddChild(second);

        cycle.AttachCamera(first);
        cycle.AttachCamera(second); // the free camera replacing the player's, mid-session

        cycle.QueueFree();
        first.QueueFree();
        second.QueueFree();
    }

    // A map with no Lighting.dat at all still has to run: the controller is built before the level is
    // known to be complete, and a null here used to mean no sky and no sun.
    [Test]
    public async Task ACycleWithNoLightingDataStillRuns()
    {
        DayNightController cycle = DayNightController.Build(lighting: null, waterMaterial: null);
        TestScene.AddChild(cycle);

        for (int i = 0; i < 3; i++)
            await NextFrame();

        Assert.NotNull(cycle.GetNodeOrNull<DirectionalLight3D>("Sun"));
        cycle.QueueFree();
    }

    // --- sun shafts ----------------------------------------------------------------------------------

    // Off by default, exactly like the game (GraphicsSettingsData.SunShaftsQuality = OFF). Worth pinning
    // because the pass is a full-screen quad on the near plane: switched on by accident it would be a
    // permanent cost, and switched on silently it would be a permanent mystery.
    [Test]
    public void TheSunShaftPassIsOffUnlessAskedFor()
    {
        DayNightController cycle = DayNightController.Build(Lighting(), waterMaterial: null);
        TestScene.AddChild(cycle);

        Assert.Null(FindShafts(cycle));

        cycle.QueueFree();
    }

    // Switched on, the quad exists but stays hidden until a camera is attached — there is no screen
    // position for the sun without one, and drawing it anyway would add an untextured quad to the frame.
    [Test]
    public async Task TheShaftQuadStaysHiddenUntilACameraIsAttached()
    {
        DayNightController cycle = await WithEnvironment("SUN_SHAFTS", "1", async () =>
        {
            DayNightController built = DayNightController.Build(Lighting(), waterMaterial: null);
            TestScene.AddChild(built);
            await NextFrame();
            return built;
        });

        SunShafts? shafts = FindShafts(cycle);
        Assert.NotNull(shafts);
        Assert.False(shafts!.GetNode<MeshInstance3D>("SunShaftsQuad").Visible);

        cycle.QueueFree();
    }

    // With a camera looking at the sun the quad is drawn and told where the sun is on screen. Looking
    // away from it hides the quad again: there is nothing to streak from behind you, and the unprojected
    // position would be meaningless.
    [Test]
    public async Task TheQuadFollowsTheCameraAndHidesWhenTheSunIsBehindIt()
    {
        DayNightController cycle = await WithEnvironment("SUN_SHAFTS", "1", async () =>
        {
            DayNightController built = DayNightController.Build(Lighting(), waterMaterial: null);
            TestScene.AddChild(built);
            return built;
        });
        SunShafts shafts = FindShafts(cycle)!;
        var sun = cycle.GetNode<DirectionalLight3D>("Sun");
        // Taken BEFORE the camera is attached, while it is still this pass's own child. Searching for it
        // afterwards would find whichever quad the tree happens to hold first — QueueFree is deferred, so
        // an earlier test's pass can still be around.
        MeshInstance3D quad = shafts.GetNode<MeshInstance3D>("SunShaftsQuad");

        var camera = new Camera3D { Current = true };
        TestScene.AddChild(camera);
        cycle.AttachCamera(camera);

        // The quad re-parents onto the camera, just inside its near plane, so it is always in frame.
        Assert.Same(camera, quad.GetParent());
        Assert.True(quad.Position.Z < 0f, "the quad was not put in front of the camera");

        // Point the camera at where the sun comes from (+Z of the light's basis), then directly away.
        Vector3 towardSun = sun.GlobalTransform.Basis.Z;
        camera.LookAtFromPosition(Vector3.Zero, towardSun * 4096f);
        await NextFrame();
        bool visibleFacingSun = quad.Visible;

        camera.LookAtFromPosition(Vector3.Zero, -towardSun * 4096f);
        await NextFrame();

        Assert.True(visibleFacingSun, "the shafts never drew while looking at the sun");
        Assert.False(quad.Visible, "the shafts kept drawing with the sun behind the camera");

        camera.QueueFree();
        cycle.QueueFree();
    }

    // SUN_SHAFTS_DEBUG traces why nothing is visible — the effect's failure mode is "I see nothing",
    // which is indistinguishable from it being switched off. It runs inside _Process, so a throw there
    // would take the frame down rather than a diagnostic.
    [Test]
    public async Task TheShaftDebugTraceRunsOnEveryBranch()
    {
        DayNightController cycle = await WithEnvironment("SUN_SHAFTS", "1", async () =>
        {
            DayNightController built = DayNightController.Build(Lighting(), waterMaterial: null);
            TestScene.AddChild(built);
            return built;
        });
        SunShafts shafts = FindShafts(cycle)!;

        await WithEnvironment("SUN_SHAFTS_DEBUG", "1", async () =>
        {
            await NextFrame(); // no camera yet: the "no camera attached" branch

            var camera = new Camera3D { Current = true };
            TestScene.AddChild(camera);
            cycle.AttachCamera(camera);
            var sun = cycle.GetNode<DirectionalLight3D>("Sun");

            camera.LookAtFromPosition(Vector3.Zero, -sun.GlobalTransform.Basis.Z * 4096f);
            await NextFrame(); // sun behind the camera: the branch that reports where it actually is

            camera.LookAtFromPosition(Vector3.Zero, sun.GlobalTransform.Basis.Z * 4096f);
            await NextFrame(); // and the one that reports the uv it settled on

            camera.QueueFree();
            return true;
        });

        Assert.NotNull(shafts);
        cycle.QueueFree();
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static SunShafts? FindShafts(Node parent)
    {
        foreach (Node child in parent.GetChildren())
            if (child is SunShafts shafts)
                return shafts;
        return null;
    }


    // Runs `body` with an environment variable set, and puts it back afterwards however that goes — a
    // leaked TIME_OF_DAY would silently freeze every later test's cycle.
    private static async Task<T> WithEnvironment<T>(string name, string value, System.Func<Task<T>> body)
    {
        string previous = OS.GetEnvironment(name);
        OS.SetEnvironment(name, value);
        try
        {
            return await body();
        }
        finally
        {
            OS.SetEnvironment(name, previous);
        }
    }

    // A 1x1 texture: the shader only needs something bindable, not something that looks like stars.
    private static ImageTexture Pixel() =>
        ImageTexture.CreateFromImage(Image.CreateEmpty(1, 1, false, Image.Format.Rgba8));

    private static WorldEnvironment? FindEnvironment(Node parent)
    {
        foreach (Node child in parent.GetChildren())
            if (child is WorldEnvironment env)
                return env;
        return null;
    }

    private static LevelLighting Lighting() => LevelLighting.Parse(new MemoryStream(BuildLightingDat()));

    // A synthetic Lighting.dat in the real layout, so none of this needs the game installed. Keyframe
    // values are spread apart on purpose: midday and midnight have to be visibly different for the
    // assertions above to mean anything.
    private static byte[] BuildLightingDat()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write((byte)12);  // version
            w.Write(281.74f);   // azimuth
            w.Write(0.1f);      // bias
            w.Write(0.2f);      // fade
            w.Write(0.32f);     // time of day
            w.Write((byte)3);   // moon phase
            w.Write(0.5f);      // sea level
            w.Write(0.6f);      // snow level
            w.Write(true);      // canRain
            w.Write(false);     // canSnow
            w.Write(1f);        // rain frequency
            w.Write(2f);        // rain duration
            w.Write(3f);        // snow frequency
            w.Write(4f);        // snow duration
            for (int k = 0; k < 4; k++)
            {
                for (int i = 0; i < 12; i++) // the 12 ELightingColor entries, RGB bytes
                {
                    w.Write((byte)(k * 60));
                    w.Write((byte)(i * 20));
                    w.Write((byte)(255 - k * 60));
                }
                w.Write(0.25f + k);          // intensity
                w.Write(0.1f * k);           // fog
                w.Write(0.2f * k);           // clouds
                w.Write(0.75f - 0.25f * k);  // shadows
                w.Write(0.25f);              // rays
            }
        }
        return ms.ToArray();
    }

    private SignalAwaiter NextFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
}
