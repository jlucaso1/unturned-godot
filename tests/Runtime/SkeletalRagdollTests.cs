using System.Collections.Generic;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Damage;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// A corpse falling a limb at a time, against the REAL zombie rig and the REAL prefab.
//
// Nothing here can be checked without an engine: the bodies are stepped by the physics server and the
// bones are written back from them, so what a unit test could reach is the arithmetic and what actually
// breaks is the wiring — a limb jointed to nothing, a body built at the origin instead of on its bone, a
// pose written into the wrong frame.
public class SkeletalRagdollTests : TestClass
{
    public SkeletalRagdollTests(Node testScene) : base(testScene) { }

    private SignalAwaiter NextPhysicsFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.PhysicsFrame);

    private static string? Install => UnturnedInstall.Find();

    private readonly List<Node> _spawned = new();

    [Cleanup]
    public void FreeSpawned()
    {
        foreach (Node node in _spawned)
            node.QueueFree();
        _spawned.Clear();
    }

    private T Track<T>(T node) where T : Node
    {
        _spawned.Add(node);
        return node;
    }

    // The rig a live zombie wears, and the ragdoll the game describes for it.
    private (CharacterSkeleton Rig, RagdollDefinition Definition)? Subject()
    {
        if (Install == null)
            return null;
        if (CharacterModel.BuildZombie(Install, isMega: false) is not CharacterSkeleton rig)
            return null;
        RagdollDefinition? definition =
            RagdollExtractor.Read(Install, RagdollExtractor.ZombiePrefab);
        if (definition == null)
        {
            rig.Free();
            return null;
        }

        TestScene.AddChild(rig);
        Track(rig);
        return (rig, definition);
    }

    // Every body the prefab describes finds its bone. A limb short is a limb that never falls.
    [Test]
    public void EveryBoneTheGameDescribesGetsABody()
    {
        if (Subject() is not var (rig, definition))
            return;

        var ragdoll = Track(new SkeletalRagdoll());
        TestScene.AddChild(ragdoll);

        Assert.True(ragdoll.Begin(rig, definition, Vector3.Zero));
        Assert.True(ragdoll.Simulating);
        // Eleven in the shipped prefab: the pelvis, spine, skull and two of each limb segment.
        Assert.Equal(definition.Bones.Count, ragdoll.LimbCount);
    }

    // The bodies are built ON their bones, not at the origin. Getting this wrong is the failure that
    // looks like a corpse exploding away from where it died.
    [Test]
    public void TheBodiesStartWhereTheBonesAre()
    {
        if (Subject() is not var (rig, definition))
            return;
        rig.Position = new Vector3(40f, 12f, -7f);

        var ragdoll = Track(new SkeletalRagdoll());
        TestScene.AddChild(ragdoll);
        Assert.True(ragdoll.Begin(rig, definition, Vector3.Zero));

        foreach (Node child in ragdoll.GetChildren())
        {
            if (child is not RigidBody3D body)
                continue;
            Assert.True(body.GlobalPosition.DistanceTo(rig.GlobalPosition) < 4f,
                $"a limb started {body.GlobalPosition} away from a rig at {rig.GlobalPosition}");
        }
    }

    // The joints hold. A chain that came apart would send limbs off on their own, which is the single
    // most visible way this can be wrong — and it is what a rig with three roots would do under
    // PhysicalBone3D, which is why this is built from explicit joints instead.
    [Test]
    public async Task TheLimbsStayTogetherWhileTheCorpseFalls()
    {
        if (Subject() is not var (rig, definition))
            return;

        var ragdoll = Track(new SkeletalRagdoll());
        TestScene.AddChild(ragdoll);
        Assert.True(ragdoll.Begin(rig, definition, Vector3.Zero));

        for (int i = 0; i < 30; i++)
            await NextPhysicsFrame();

        // Nothing to stand on in the test scene, so they all fall — together. A body that lost its joint
        // would drift away from the rest instead of falling with them.
        Vector3 centre = Vector3.Zero;
        var bodies = new List<RigidBody3D>();
        foreach (Node child in ragdoll.GetChildren())
            if (child is RigidBody3D body)
            {
                bodies.Add(body);
                centre += body.GlobalPosition;
            }

        centre /= bodies.Count;
        foreach (RigidBody3D body in bodies)
        {
            Assert.True(body.GlobalPosition.DistanceTo(centre) < 4f,
                $"a limb drifted to {body.GlobalPosition} from a corpse centred at {centre}");
        }
    }

    // And the SKELETON follows the bodies: that is what makes the skinned mesh a corpse rather than a
    // statue standing where the physics used to be.
    [Test]
    public async Task TheBonesFollowTheBodies()
    {
        if (Subject() is not var (rig, definition))
            return;

        int skull = rig.FindBone("Skull");
        Assert.True(skull >= 0);
        Transform3D before = rig.GetBoneGlobalPose(skull);

        var ragdoll = Track(new SkeletalRagdoll());
        TestScene.AddChild(ragdoll);
        Assert.True(ragdoll.Begin(rig, definition, new Vector3(0f, 0f, -400f)));

        for (int i = 0; i < 30; i++)
            await NextPhysicsFrame();

        Assert.True(rig.GetBoneGlobalPose(skull).Origin.DistanceTo(before.Origin) > 0.05f,
            "the skull bone never moved, so the mesh would still be standing");
    }

    // The shove goes in, and the corpse travels the way it was hit.
    [Test]
    public async Task AShoveThrowsTheCorpseInItsDirection()
    {
        if (Subject() is not var (rig, definition))
            return;

        var ragdoll = Track(new SkeletalRagdoll());
        TestScene.AddChild(ragdoll);
        Assert.True(ragdoll.Begin(rig, definition,
            RagdollForce.Thrown(RagdollForce.Impulse(Vector3.Forward, 400), 0f, 0f)));

        for (int i = 0; i < 20; i++)
            await NextPhysicsFrame();

        int spine = rig.FindBone("Spine");
        Assert.True(rig.GetBoneGlobalPose(spine).Origin.Z < -0.1f,
            "the corpse did not travel down -Z");
    }

    // A rig the definition does not fit — no bones by those names — reports failure rather than standing
    // up an empty simulator, so the caller can fall back to tumbling the body whole.
    [Test]
    public void ARigWithNoneOfTheBonesIsRefused()
    {
        var bare = Track(new Skeleton3D());
        bare.AddBone("NotARagdollBone");
        TestScene.AddChild(bare);

        var ragdoll = Track(new SkeletalRagdoll());
        TestScene.AddChild(ragdoll);

        Assert.False(ragdoll.Begin(bare, new RagdollDefinition(new[]
        {
            new RagdollBone("Skeleton", "", 1f, Vector3.Zero, Vector3.One, 0, 0, 30, 30),
            new RagdollBone("Spine", "Skeleton", 1f, Vector3.Zero, Vector3.One, 0, 0, 30, 30),
        }), Vector3.Zero));
        Assert.False(ragdoll.Simulating);
    }

    [Test]
    public void AnEmptyDefinitionIsRefused()
    {
        var bare = Track(new Skeleton3D());
        TestScene.AddChild(bare);
        var ragdoll = Track(new SkeletalRagdoll());
        TestScene.AddChild(ragdoll);

        Assert.False(ragdoll.Begin(bare, new RagdollDefinition(System.Array.Empty<RagdollBone>()),
            Vector3.Zero));
    }
}
