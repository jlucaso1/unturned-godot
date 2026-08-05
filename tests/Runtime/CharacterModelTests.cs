using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Importing the game's own characters: the player, their first-person arms, and the two zombie looks.
//
// Two properties carry most of the weight here, and neither is about the geometry.
//
// The first is that a character is OPTIONAL. Every entry point answers "nothing" rather than throwing
// when the game is not installed or a prefab cannot be parsed, because the caller's fallback is a
// placeholder figure and a session that refused to start without a rig would be a session nobody without
// the game could run at all. The body and the arms are even caught separately: sharing one catch would
// let an unsupported mesh in the arms subtree throw away a body that imported perfectly.
//
// The second is that importing happens ONCE. Opening resources.assets costs ~100 ms and tens of MB of
// transient heap, and it happens inside the load's character stage — so both player rigs come out of one
// read, both zombie looks come out of one import, and every zombie after the first is a clone that
// shares the template's mesh and decoded clips.
//
// The parts that need the installed game say so and return without it; UG_REQUIRE_REAL_DATA=1 makes that
// a failure, so the job that fetches the content cannot pass by finding nothing.
public class CharacterModelTests : TestClass
{
    public CharacterModelTests(Node testScene) : base(testScene) { }

    // Without the game there is no character, from any entry point — and no throw. The caller draws a
    // placeholder figure, which is what someone without the game installed plays as.
    [Test]
    public void WithoutTheGameThereIsNoCharacter()
    {
        Assert.Null(CharacterModel.Build(Missing));
        Assert.Null(CharacterModel.BuildZombie(Missing, isMega: false));
        Assert.Null(CharacterModel.BuildZombie(Missing, isMega: true));

        CharacterModel.PlayerRigs rigs = CharacterModel.BuildPlayerRigs(Missing);
        Assert.Null(rigs.Body);
        Assert.Null(rigs.Viewmodel);

        (Node3D? normal, Node3D? mega) = CharacterModel.BuildZombieTemplates(Missing);
        Assert.Null(normal);
        Assert.Null(mega);
    }

    // Cloning nothing is nothing. The zombie view clones a template it may never have got, and every
    // spawn would otherwise have to ask whether the warm-up had succeeded.
    [Test]
    public void CloningNothingIsNothing()
    {
        Assert.Null(CharacterModel.Clone(null));
    }

    // Cloning something that is not a rig is nothing either. An import can produce a static bind-pose
    // mesh — geometry whose skinning did not decode — and a clone of that could not be posed, so it
    // would hang in one frozen attitude rather than animate.
    [Test]
    public void CloningSomethingThatIsNotARigIsNothing()
    {
        var plain = new Node3D();
        try
        {
            Assert.Null(CharacterModel.Clone(plain));
        }
        finally
        {
            plain.Free();
        }
    }

    // --- the installed game --------------------------------------------------------------------------

    // The player's body imports as a posable rig with bones, a mesh and decoded clips. "Posable" is the
    // load-bearing word: a static bind-pose mesh would draw correctly and never move.
    [Test]
    public void ThePlayersBodyImportsAsAPosableRig()
    {
        if (!Installed(out string install))
            return;

        CharacterModel.PlayerRigs rigs = CharacterModel.BuildPlayerRigs(install);
        try
        {
            var body = Assert.IsType<CharacterSkeleton>(rigs.Body);
            Assert.True(body.GetBoneCount() > 0);
            Assert.True(body.HasAnyPose, "the body imported with no decoded clips, so it cannot animate");
            Assert.NotNull(body.GetChildOrNull<MeshInstance3D>(0));
        }
        finally
        {
            rigs.Body?.Free();
            rigs.Viewmodel?.Free();
        }
    }

    // First person gets a rig too. The prefab's own arms are Model_0, whose skin weights live in the
    // compressed vertex stream this pipeline does not decode — so the import there produces a static
    // mesh, and the body rig stands in. That fallback is why first person has a swing at all: before it,
    // third person punched and first person did not, and it read as a missing clip rather than a missing
    // rig.
    [Test]
    public void FirstPersonGetsARigEvenWhenTheArmsDoNotDecode()
    {
        if (!Installed(out string install))
            return;

        CharacterModel.PlayerRigs rigs = CharacterModel.BuildPlayerRigs(install);
        try
        {
            if (rigs.Body == null)
                return; // covered by the body test; nothing to stand in from

            var arms = Assert.IsType<CharacterSkeleton>(rigs.Viewmodel);
            Assert.True(arms.HasAnyPose, "the arms cannot be posed, so they would freeze mid-swing");
        }
        finally
        {
            rigs.Body?.Free();
            rigs.Viewmodel?.Free();
        }
    }

    // The punch clips arrive mixed onto one arm each, over the game's REAL skeleton. This is asserted
    // against the imported rig rather than a stand-in because the data is what makes it necessary:
    // Punch_Left and Punch_Right are authored over all sixteen bones, legs included, so an unmixed swing
    // replaces the walk cycle and the legs stand still for its whole 0.63 s.
    //
    // Both rigs are checked. First person is a CLONE of the body, and a clone that lost its mixing
    // transforms would swing full-body from inside the head while the third-person copy behaved.
    [Test]
    public void ThePunchClipsAreMixedOntoOneArmEach()
    {
        if (!Installed(out string install))
            return;

        CharacterModel.PlayerRigs rigs = CharacterModel.BuildPlayerRigs(install);
        try
        {
            foreach (Node3D? node in new[] { rigs.Body, rigs.Viewmodel })
            {
                if (node is not CharacterSkeleton rig || rig.Clips.Count == 0)
                    continue;

                foreach ((string clip, string arm, string other) in new[]
                {
                    ("Punch_Left", "Left_Shoulder", "Right_Shoulder"),
                    ("Punch_Right", "Right_Shoulder", "Left_Shoulder"),
                })
                {
                    Assert.True(rig.Mixes.ContainsKey(clip), $"{rig.Name}: {clip} was never mixed");
                    BoneMask mask = rig.Mixes[clip]
                        ?? throw new Xunit.Sdk.XunitException($"{rig.Name}: {clip} restricts nothing");

                    // The whole limb, and only that limb.
                    foreach (string bone in new[] { arm, arm.Replace("Shoulder", "Arm"),
                                 arm.Replace("Shoulder", "Hand"), arm.Replace("Shoulder", "Hook") })
                        Assert.True(mask.Contains(rig.FindBone(bone)), $"{rig.Name}: {clip} misses {bone}");

                    foreach (string bone in new[] { other, "Spine", "Skull",
                                 "Left_Hip", "Left_Leg", "Left_Foot", "Right_Hip", "Right_Leg", "Right_Foot" })
                        Assert.False(mask.Contains(rig.FindBone(bone)), $"{rig.Name}: {clip} reaches {bone}");
                }
            }
        }
        finally
        {
            rigs.Body?.Free();
            rigs.Viewmodel?.Free();
        }
    }

    // Both zombie looks come out of ONE import. The mega is the same character in a different skin, and
    // paying a second full read for it would put a synchronous character import in the frame a player
    // first meets one.
    [Test]
    public void BothZombieLooksComeOutOfOneImport()
    {
        if (!Installed(out string install))
            return;

        (Node3D? normal, Node3D? mega) = CharacterModel.BuildZombieTemplates(install);
        try
        {
            Assert.NotNull(normal);
            Assert.NotNull(mega);
            Assert.NotSame(normal, mega);

            // The mega is a different colour, and it says so through an override rather than by editing
            // the material the normal template is still using.
            MeshInstance3D? megaBody = mega!.GetChildOrNull<MeshInstance3D>(0);
            MeshInstance3D? normalBody = normal!.GetChildOrNull<MeshInstance3D>(0);
            if (megaBody?.MaterialOverride is ShaderMaterial megaMaterial && normalBody != null)
            {
                Assert.NotSame(megaMaterial, normalBody.MaterialOverride);
                Assert.NotEqual(default, megaMaterial.GetShaderParameter("skin_color"));
            }
        }
        finally
        {
            normal?.Free();
            mega?.Free();
        }
    }

    // A clone shares the template's mesh rather than copying it. This is what makes a city of zombies
    // affordable: the game data is decoded once per look, and every entity after the first is bones.
    [Test]
    public void ACloneSharesTheTemplatesMesh()
    {
        if (!Installed(out string install))
            return;

        Node3D? template = CharacterModel.BuildZombie(install, isMega: false);
        if (template == null)
            return;

        CharacterSkeleton? clone = CharacterModel.Clone(template);
        try
        {
            Assert.NotNull(clone);
            Assert.Equal(((CharacterSkeleton)template).GetBoneCount(), clone!.GetBoneCount());

            MeshInstance3D? source = template.GetChildOrNull<MeshInstance3D>(0);
            MeshInstance3D? copy = clone.GetChildOrNull<MeshInstance3D>(0);

            // Both asserted: a template with no mesh child would otherwise throw a NullReferenceException
            // out of the line below and report as a crash rather than as the expectation that failed.
            Assert.NotNull(source);
            Assert.NotNull(copy);
            Assert.Same(source!.Mesh, copy!.Mesh);

            // And the decoded clips, which are the expensive half of the import.
            Assert.Equal(((CharacterSkeleton)template).Clips.Count, clone.Clips.Count);
        }
        finally
        {
            clone?.Free();
            template.Free();
        }
    }

    // --- helpers -------------------------------------------------------------------------------------

    private const string Missing = "/nonexistent-unturned";

    private static bool Installed(out string install)
    {
        install = "";
        string? found = Assets.UnturnedInstall.Find();
        if (found == null)
        {
            if (System.Environment.GetEnvironmentVariable("UG_REQUIRE_REAL_DATA") == "1")
            {
                throw new System.IO.IOException(
                    "UG_REQUIRE_REAL_DATA=1 but no Unturned install is present; this run exists to prove "
                    + "these tests execute");
            }

            Log.Print("[runtime-tests] skipping: no Unturned install "
                + "(set UNTURNED_PATH or run ./scripts/fetch-game-data.sh)");
            return false;
        }

        install = found;
        return true;
    }
}
