using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.UI;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The client's own raycast for the hitmarker — PlayerEquipment.punch's `if (channel.IsLocalPlayer)` half.
//
// What it must get right is the LADDER: a body beats a breakable, a breakable beats a wall, and a wall
// flashes nothing at all. The crosshair says whether you hurt something, not whether you touched
// something — the sound and the mark already say that.
public class PunchHitmarkTests : TestClass
{
    public PunchHitmarkTests(Node testScene) : base(testScene) { }

    private SignalAwaiter NextPhysicsFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.PhysicsFrame);

    private static ObjectAsset Parse(string dat)
    {
        Assert.True(ObjectAsset.TryParse(DatParser.Parse(dat), null, out ObjectAsset? asset));
        return asset;
    }

    // A rubble prop a bare hand can break.
    private static ObjectAsset Breakable() => Parse("""
        GUID a19b3ec55a2046668611c9d2775efd99
        Type Medium
        ID 60
        Rubble Destroy
        Rubble_Health 75
        """);

    // A rubble prop that wants a blade — a fist cannot touch it, so no mark.
    private static ObjectAsset NeedsABlade() => Parse("""
        GUID b19b3ec55a2046668611c9d2775efd99
        Type Medium
        ID 61
        Rubble Destroy
        Rubble_Health 75
        Rubble_Blade_ID 3
        """);

    private static ObjectAsset Invulnerable() => Parse("""
        GUID c19b3ec55a2046668611c9d2775efd99
        Type Medium
        ID 62
        Rubble Destroy
        Rubble_Health 75
        Rubble_Invulnerable
        """);

    // A tree that opts in to fists, and one that does not — the resource path's own gate.
    private static ObjectAsset VulnerableTree() => Parse("""
        GUID d19b3ec55a2046668611c9d2775efd99
        Type Resource
        ID 63
        Health 800
        Vulnerable_To_Fists
        """);

    private static ObjectAsset ToughTree() => Parse("""
        GUID e19b3ec55a2046668611c9d2775efd99
        Type Resource
        ID 64
        Health 800
        """);

    // The asset gate, which is the whole of the BUILD decision. Kept as its own test because it is the
    // part that mirrors PunchDamageResolver, and the two disagreeing would mean a mark for a hit that
    // does nothing (or none for a hit that does).
    [Test]
    public void OnlyAssetsAFistCanHurtEarnAMark()
    {
        Assert.True(PunchHitmark.CanFistDamage(Breakable()));
        Assert.True(PunchHitmark.CanFistDamage(VulnerableTree()));

        Assert.False(PunchHitmark.CanFistDamage(NeedsABlade()));
        Assert.False(PunchHitmark.CanFistDamage(Invulnerable()));
        Assert.False(PunchHitmark.CanFistDamage(ToughTree()));
    }

    private PunchHitmark Probe(ObjectAssetDatabase? assets, ZombiesView? zombies, Node3D? world) =>
        new(() => assets, () => zombies, () => world?.GetWorld3D());

    // A body in a wall-less world: entity, and critical when the ray enters the skull band.
    [Test]
    public void AZombieIsAnEntityHitAndItsSkullIsCritical()
    {
        var zombies = new ZombiesView();
        TestScene.AddChild(zombies);
        zombies.PlantForTest(new Vector3(0f, 0f, -1f));

        PunchHitmark probe = Probe(null, zombies, null);

        // Chest height on a 2 m body.
        Assert.Equal(EPlayerHit.Entity, probe.Resolve(new Vector3(0f, 1.2f, 0f), Vector3.Forward));
        // And the head.
        Assert.Equal(EPlayerHit.Critical, probe.Resolve(new Vector3(0f, 1.75f, 0f), Vector3.Forward));

        zombies.QueueFree();
    }

    [Test]
    public void AnEmptySwingFlashesNothing()
    {
        PunchHitmark probe = Probe(null, null, null);

        Assert.Equal(EPlayerHit.None, probe.Resolve(Vector3.Zero, Vector3.Forward));
        Assert.Equal(EPlayerHit.None, probe.Resolve(Vector3.Zero, Vector3.Zero));
    }

    // A breakable prop's body: BUILD. The asset is what says so, looked up by the GUID the body's name
    // carries — the same identification the server's damage path makes.
    [Test]
    public async Task ABreakablePropIsABuildHit()
    {
        var assets = new ObjectAssetDatabase();
        assets.Add(Breakable());
        Node3D world = Wall(Breakable().Guid, -1f);
        await NextPhysicsFrame();

        PunchHitmark probe = Probe(assets, null, world);

        Assert.Equal(EPlayerHit.Build, probe.Resolve(Vector3.Zero, Vector3.Forward));

        world.QueueFree();
    }

    // A prop a fist cannot hurt is a wall as far as the crosshair is concerned, even though it is a
    // perfectly good body with an asset behind it.
    [Test]
    public async Task APropAFistCannotHurtFlashesNothing()
    {
        var assets = new ObjectAssetDatabase();
        assets.Add(NeedsABlade());
        Node3D world = Wall(NeedsABlade().Guid, -1f);
        await NextPhysicsFrame();

        Assert.Equal(EPlayerHit.None, Probe(assets, null, world).Resolve(Vector3.Zero, Vector3.Forward));

        world.QueueFree();
    }

    // Terrain and anything else nobody named: no asset, no mark.
    [Test]
    public async Task AnUnnamedBodyFlashesNothing()
    {
        Node3D world = Wall(System.Guid.Empty, -1f);
        await NextPhysicsFrame();

        Assert.Equal(EPlayerHit.None,
            Probe(new ObjectAssetDatabase(), null, world).Resolve(Vector3.Zero, Vector3.Forward));

        world.QueueFree();
    }

    // The ladder's point: a wall in front of a zombie stops the fist, so the zombie is not marked. This
    // is the same shortening the server's own resolution does, and getting it wrong would flash a hit
    // through a fence.
    [Test]
    public async Task AWallInFrontOfAZombieTakesTheReach()
    {
        var zombies = new ZombiesView();
        TestScene.AddChild(zombies);
        zombies.PlantForTest(new Vector3(0f, 0f, -1.5f));

        var assets = new ObjectAssetDatabase();
        assets.Add(NeedsABlade());
        Node3D world = Wall(NeedsABlade().Guid, -0.5f);
        await NextPhysicsFrame();

        // The wall is nearer than the zombie and cannot be hurt, so nothing flashes.
        Assert.Equal(EPlayerHit.None,
            Probe(assets, zombies, world).Resolve(new Vector3(0f, 1.2f, 0f), Vector3.Forward));

        world.QueueFree();
        zombies.QueueFree();
    }

    // Past the fist's 1.75 m reach nothing is marked, however solid it is.
    [Test]
    public async Task NothingBeyondTheFistsReachIsMarked()
    {
        var assets = new ObjectAssetDatabase();
        assets.Add(Breakable());
        Node3D world = Wall(Breakable().Guid, -4f);
        await NextPhysicsFrame();

        Assert.Equal(EPlayerHit.None, Probe(assets, null, world).Resolve(Vector3.Zero, Vector3.Forward));

        world.QueueFree();
    }

    // A solid body `z` metres down -Z, named for `asset`.
    private Node3D Wall(System.Guid asset, float z)
    {
        var bodies = new InstancedStaticBodies { Name = "Wall" };
        bodies.Add(ObjectCollisionNames.For(asset),
            new Shape3D[] { new BoxShape3D { Size = new Vector3(4f, 4f, 0.2f) } },
            new[] { new CollisionPlacement(0, new Transform3D(Basis.Identity, new Vector3(0f, 0f, z)), 0) },
            CollisionLayers.World);
        TestScene.AddChild(bodies);
        return bodies;
    }

}
