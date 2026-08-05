using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The screenshot/inspection camera. Small, and entirely made of things only an engine can answer: input
// events, the global input state, and a transform.
//
// The layout rule is the part worth guarding. WASD is a SHAPE — a cluster under the left hand — so the
// camera asks for physical key positions rather than the keys that print those letters. Ask for the
// keycode instead and an AZERTY keyboard drives the camera from a scattering of keys nobody would press.
// A test that presses physical keys is the only thing that can tell the two apart.
public class FreeCameraTests : TestClass
{
    public FreeCameraTests(Node testScene) : base(testScene) { }

    // Every key this class presses. Input.ParseInputEvent is QUEUED, not applied — a release posted at
    // the end of one test is still pending when the next one starts, and the camera it builds then drifts
    // under a key nobody pressed. Clearing them and letting a frame pass is what makes each test start
    // from a keyboard at rest.
    private static readonly Key[] UsedKeys =
        { Key.W, Key.A, Key.S, Key.D, Key.E, Key.Q, Key.Shift };

    [Setup]
    public async Task ReleaseEveryKey()
    {
        foreach (Key key in UsedKeys)
            PressPhysical(key, false);
        await NextFrame(); // the releases are queued; this is where they land
    }

    // _Ready adopts the rotation the camera was placed with, rather than resetting the view: SHOT_CAM
    // hands it an authored angle and the first mouse movement must continue from there, not jump.
    [Test]
    public void ReadyAdoptsTheAuthoredAngleAndSeesToTheHorizon()
    {
        var cam = new FreeCamera { RotationDegrees = new Vector3(-30f, 45f, 0f) };
        TestScene.AddChild(cam);

        Assert.True(cam.Current);
        // The map spans several kilometres; the engine's 4000 m default clips distant terrain.
        Assert.Equal(8000f, cam.Far);

        // Turning from here must continue from the authored angle. A camera that reset would land on
        // the yaw of the motion alone.
        cam._Input(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Left });
        cam._Input(new InputEventMouseMotion { Relative = new Vector2(0f, 0f) });
        Assert.True(Mathf.Abs(cam.RotationDegrees.Y - 45f) < 0.01f,
            $"the camera did not keep its authored yaw: {cam.RotationDegrees.Y}");

        Release(cam);
    }

    // Clicking captures the mouse; only then does motion turn the camera. Without the gate, moving the
    // cursor over a window that has not been clicked would spin the view.
    [Test]
    public void MotionOnlyTurnsTheCameraOnceTheMouseIsCaptured()
    {
        var cam = new FreeCamera();
        TestScene.AddChild(cam);

        cam._Input(new InputEventMouseMotion { Relative = new Vector2(100f, 0f) });
        Assert.Equal(0f, cam.Rotation.Y); // not captured: ignored

        cam._Input(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Left });
        cam._Input(new InputEventMouseMotion { Relative = new Vector2(100f, 0f) });

        // Yaw decreases as the mouse moves right (Godot's Y axis turns the other way).
        Assert.True(cam.Rotation.Y < -0.05f, $"the camera did not turn: {cam.Rotation.Y}");

        Release(cam);
    }

    // Escape releases the mouse and stops the camera turning — the way out of a captured window.
    [Test]
    public void EscapeReleasesTheMouseAndStopsTheTurning()
    {
        var cam = new FreeCamera();
        TestScene.AddChild(cam);
        cam._Input(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Left });

        cam._Input(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        float afterEscape = cam.Rotation.Y;
        cam._Input(new InputEventMouseMotion { Relative = new Vector2(100f, 0f) });

        Assert.Equal(afterEscape, cam.Rotation.Y);
        Release(cam);
    }

    // Pitch is clamped just short of straight up and straight down. Past vertical the view rolls over,
    // which is disorienting and is why every mouselook does this.
    [Test]
    public void PitchStopsJustShortOfVertical()
    {
        var cam = new FreeCamera();
        TestScene.AddChild(cam);
        cam._Input(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Left });

        cam._Input(new InputEventMouseMotion { Relative = new Vector2(0f, -100000f) }); // heave it up
        Assert.True(cam.Rotation.X <= (Mathf.Pi / 2f) - 0.009f, $"pitch ran past vertical: {cam.Rotation.X}");
        Assert.True(cam.Rotation.X > (Mathf.Pi / 2f) - 0.02f, $"pitch stopped short of the clamp: {cam.Rotation.X}");

        cam._Input(new InputEventMouseMotion { Relative = new Vector2(0f, 200000f) }); // and back down
        Assert.True(cam.Rotation.X >= -(Mathf.Pi / 2f) + 0.009f, $"pitch ran past vertical: {cam.Rotation.X}");

        Release(cam);
    }

    // The layout rule, asserted where it actually lives: a PHYSICAL key press moves the camera. This is
    // the test that would fail if the code went back to asking for keycodes.
    [Test]
    public async Task PhysicalKeysDriveTheCamera()
    {
        var cam = new FreeCamera();
        TestScene.AddChild(cam);
        await NextFrame(); // _Ready and one settled frame before anything is pressed

        Vector3 start = cam.Position;
        PressPhysical(Key.W, true);
        await NextFrame();
        await NextFrame();
        PressPhysical(Key.W, false);

        // W is forward: -Z in the camera's own basis, which at the identity rotation is world -Z.
        Assert.True(cam.Position.Z < start.Z - 0.01f, $"W did not move the camera forward: {cam.Position}");

        Release(cam);
    }

    // Each direction key maps to its own axis. Wired one axis wrong and the camera strafes when asked to
    // rise, which is the kind of thing nobody notices until they are trying to frame a shot.
    [Test]
    public async Task EveryDirectionKeyMovesItsOwnAxis()
    {
        var cases = new (Key Key, System.Func<Vector3, Vector3, bool> Moved, string What)[]
        {
            (Key.S, (a, b) => b.Z > a.Z, "S should move back"),
            (Key.A, (a, b) => b.X < a.X, "A should strafe left"),
            (Key.D, (a, b) => b.X > a.X, "D should strafe right"),
            (Key.E, (a, b) => b.Y > a.Y, "E should rise"),
            (Key.Q, (a, b) => b.Y < a.Y, "Q should sink"),
        };

        foreach ((Key key, System.Func<Vector3, Vector3, bool> moved, string what) in cases)
        {
            var cam = new FreeCamera();
            TestScene.AddChild(cam);
            await NextFrame();

            Vector3 start = cam.Position;
            PressPhysical(key, true);
            await NextFrame();
            await NextFrame();
            PressPhysical(key, false);

            Assert.True(moved(start, cam.Position), $"{what}; it went {start} -> {cam.Position}");
            Release(cam);
        }
    }

    // Shift multiplies the speed: the map is kilometres across and crossing it at walking pace is the
    // difference between a usable inspection camera and an unusable one.
    [Test]
    public async Task ShiftBoostsTheSpeed()
    {
        float plain = await TravelledPerFrame(boost: false);
        float boosted = await TravelledPerFrame(boost: true);

        Assert.True(boosted > plain * 2f,
            $"shift did not boost the speed: {plain:0.000} -> {boosted:0.000} per frame");
    }

    // An idle camera must not write Position at all: writing it dirties the transform and re-sends the
    // camera to the RenderingServer every frame even when the view has not moved. Asserted as "the
    // transform is untouched", which is the observable form of that.
    [Test]
    public async Task AnIdleCameraDoesNotMove()
    {
        var cam = new FreeCamera { Position = new Vector3(5f, 6f, 7f) };
        TestScene.AddChild(cam);

        for (int i = 0; i < 3; i++)
            await NextFrame();

        Assert.Equal(new Vector3(5f, 6f, 7f), cam.Position);
        Release(cam);
    }

    // --- helpers -------------------------------------------------------------------------------------

    private const int Frames = 4; // enough that one frame's delta jitter does not decide the comparison

    private async Task<float> TravelledPerFrame(bool boost)
    {
        var cam = new FreeCamera();
        TestScene.AddChild(cam);
        await NextFrame();

        if (boost)
            PressPhysical(Key.Shift, true);
        PressPhysical(Key.W, true);
        await NextFrame(); // the presses land here; movement starts on the frame after

        Vector3 start = cam.Position;
        for (int i = 0; i < Frames; i++)
            await NextFrame();
        Vector3 after = cam.Position;

        PressPhysical(Key.W, false);
        if (boost)
            PressPhysical(Key.Shift, false);

        Release(cam);
        // Per frame, so the two cases compare even if their frames were not the same length.
        return start.DistanceTo(after) / Frames;
    }

    // Feeds the engine a physical key press, which is what Input.IsPhysicalKeyPressed answers from. The
    // physical keycode is the point: a plain Keycode would leave IsPhysicalKeyPressed false and every
    // movement test would pass for the wrong reason.
    private static void PressPhysical(Key key, bool pressed) =>
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Pressed = pressed });

    // Cameras are freed rather than queued, so the next test's camera is the Current one immediately —
    // a queued free lands at the end of the frame and two cameras would both claim Current until then.
    private static void Release(FreeCamera cam)
    {
        cam.GetParent()?.RemoveChild(cam);
        cam.Free();
    }

    private SignalAwaiter NextFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
}
