using System;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Footsteps and landings for ONE character, local or remote.
//
// Nothing here comes off the wire. Every client works out every player's movement sounds from the
// replicated simulation state — stance, moving, grounded, position — exactly as the game does for
// non-local channels, because sending them would be a packet per footstep for something both ends can
// already derive.
//
// What is held below is the SILENCE, and that is not a lesser half. Between the splat, the landscape
// material table and the audio bank there are four separate ways for a surface to have no sound — an
// unloaded tile, a material the landscape does not name, a surface with no definition for the event, a
// definition whose clips have not been extracted yet — and every one of them happens routinely during a
// load. A throw on any would take down the walking player rather than the sound.
//
// The audible half needs an extracted .ogg cache to exist on disk, which is a load rather than a test;
// what plays through it is OneShotAudio's, and that is covered where it lives.
public class MovementAudioTests : TestClass
{
    public MovementAudioTests(Node testScene) : base(testScene) { }

    // A player walking over ground the splat has no tile for is silent. This is every session's first
    // seconds: the terrain streams in tile by tile, and the player is already moving.
    [Test]
    public void WalkingWhereNoTerrainHasLoadedYetIsSilent()
    {
        using var harness = new Harness(TestScene);

        harness.Walk(seconds: 4f);

        Assert.Equal(0, harness.Voices);
    }

    // A tile that IS loaded, but whose material the landscape table cannot name, is silent too. A
    // workshop map's terrain layers are defined by its mod rather than by the game, so a map installed
    // without its mod lands exactly here.
    [Test]
    public void ASurfaceTheLandscapeCannotNameIsSilent()
    {
        using var harness = new Harness(TestScene);
        harness.Splat.Add(0, 0, new byte[Landscape.SPLATMAP_RESOLUTION * Landscape.SPLATMAP_RESOLUTION],
            new[] { Guid.NewGuid() });

        harness.Walk(seconds: 4f);

        Assert.Equal(0, harness.Voices);
    }

    // Landing is the other event, and it is silent on the same terms. A player who joins mid-air — the
    // spawn drop — lands before any of the audio cache exists.
    [Test]
    public void LandingBeforeTheAudioExistsIsSilent()
    {
        using var harness = new Harness(TestScene, startGrounded: false);

        harness.Audio.Tick(EPlayerStance.Stand, moving: false, grounded: true, Vector3.Zero, 0.1f);

        Assert.Equal(0, harness.Voices);
    }

    // A climbing player is heard on the ladder rather than on whatever is far below them — the game
    // hardcodes it, and the comment in its source is "2021-11-16: why does climbing use tile? Sigh."
    // The override means the splat is never consulted, which is the branch this reaches.
    [Test]
    public void AClimbingPlayerIsHeardOnTheLadderRatherThanTheGround()
    {
        using var harness = new Harness(TestScene);

        harness.Walk(seconds: 4f, stance: EPlayerStance.Climb);

        // With no bank there is nothing to play either way; what is covered is that a climber does not
        // fall through to the ground under them, which on a tall ladder is not loaded at all.
        Assert.Equal(0, harness.Voices);
    }

    // Prone is silent by rule rather than by circumstance: no footsteps, and no landing sound either.
    [Test]
    public void ProneIsSilentByRule()
    {
        using var harness = new Harness(TestScene);

        harness.Walk(seconds: 10f, stance: EPlayerStance.Prone);

        Assert.Equal(0, harness.Voices);
        Assert.True(FootstepConfig.IsSilentStance(EPlayerStance.Prone));
    }

    // --- helpers -------------------------------------------------------------------------------------

    private sealed class Harness : IDisposable
    {
        private readonly Node _testScene;
        private readonly OneShotAudio _voicePool;

        public SplatSampler Splat { get; } = new();
        public MovementAudio Audio { get; }

        public Harness(Node testScene, bool startGrounded = true)
        {
            _testScene = testScene;
            _voicePool = OneShotAudio.Create(new AudioDefLibrary(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "unturned-godot-no-audio-cache")));
            testScene.AddChild(_voicePool);

            Audio = new MovementAudio(new PhysicsMaterialBank(), new LandscapePhysics(), Splat,
                _voicePool, startGrounded, _ => "core");
        }

        // A voice only exists once something has played through it, so the pool's child count is the
        // count of sounds this harness has made.
        public int Voices => _voicePool.GetChildCount();

        // Walk long enough for the footstep clock to fire several times: the interval is 2.1 / speed.
        public void Walk(float seconds, EPlayerStance stance = EPlayerStance.Stand)
        {
            for (float t = 0f; t < seconds; t += 0.1f)
                Audio.Tick(stance, moving: true, grounded: true, new Vector3(t, 0f, 0f), 0.1f);
        }

        public void Dispose()
        {
            _testScene.RemoveChild(_voicePool);
            _voicePool.Free();
        }
    }
}
