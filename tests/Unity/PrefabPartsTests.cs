using System.Collections.Generic;
using Godot;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

// The rules that pick which of a prefab's renderable children get drawn. The shapes exercised here are the
// ones the shipped content actually uses; RealDataTests checks the same prefabs through the real bundle.
public class PrefabPartsTests
{
    [Theory]
    [InlineData("Dead", true)]
    [InlineData("Ragdoll", true)]
    [InlineData("Alive", false)]
    [InlineData("Model_0", false)]
    [InlineData("Object", false)]
    [InlineData("", false)]
    // Unturned's own names, matched exactly: a part called "Deadbolt" is not a destroyed state.
    [InlineData("Deadbolt", false)]
    public void IsHiddenState_MatchesTheDestroyedStateNodes(string name, bool hidden) =>
        Assert.Equal(hidden, PrefabParts.IsHiddenState(name));

    [Theory]
    [InlineData("Model_0", EPrefabLevel.Lod0)]
    [InlineData("Foliage_0", EPrefabLevel.Lod0)]
    [InlineData("Model_1", EPrefabLevel.Lod1)]
    [InlineData("Alive", EPrefabLevel.Unlevelled)]
    [InlineData("Object", EPrefabLevel.Unlevelled)]
    public void LevelOf_ReadsTheSuffixOnAnObject(string part, EPrefabLevel expected) =>
        Assert.Equal(expected, PrefabParts.LevelOf(part, isVehicle: false));

    [Theory]
    [InlineData("Model_0", EPrefabLevel.Lod0)]
    [InlineData("Model_1", EPrefabLevel.Lod1)]
    [InlineData("Model_2", EPrefabLevel.Excluded)]
    [InlineData("DepthMask", EPrefabLevel.Excluded)]
    // Seat_0/Seat_1 are the driver's and the passenger's seat, not two levels of one seat.
    [InlineData("Seat_0", EPrefabLevel.Always)]
    [InlineData("Seat_1", EPrefabLevel.Always)]
    [InlineData("Headlights", EPrefabLevel.Always)]
    public void LevelOf_TakesOnlyTheModelChainOnAVehicle(string part, EPrefabLevel expected) =>
        Assert.Equal(expected, PrefabParts.LevelOf(part, isVehicle: true));

    [Fact]
    public void Resolve_KeepsTheAuthoredLevelsApart()
    {
        var set = new PrefabPartSet<string>();
        set.Add("radar", "Model_0", isVehicle: false, "near");
        set.Add("radar", "Model_1", isVehicle: false, "far");

        (Dictionary<string, List<string>> near, Dictionary<string, List<string>> far) = set.Resolve();

        Assert.Equal(new[] { "near" }, near["radar"]);
        Assert.Equal(new[] { "far" }, far["radar"]);
    }

    // Camera #1 and Bench #1: the mesh hangs off "Alive" with no level in the name. Every such part has to
    // survive — reducing the prefab to whichever one the reader met first is how a multi-part model came
    // out missing pieces, and which piece it kept was only ever file order.
    [Fact]
    public void Resolve_DrawsEveryPartOfAPrefabThatAuthoredNoLevels()
    {
        var set = new PrefabPartSet<string>();
        set.Add("shed", "Base", isVehicle: false, "base");
        set.Add("shed", "Roof", isVehicle: false, "roof");

        (Dictionary<string, List<string>> near, Dictionary<string, List<string>> far) = set.Resolve();

        Assert.Equal(new[] { "base", "roof" }, near["shed"]);
        Assert.False(far.ContainsKey("shed"));
    }

    // An unlevelled part next to an authored LOD-0 set is still dropped: that is where the "Editor" markers
    // and harvestable "Forage" nodes live, and drawing them would put editor-only geometry in the world.
    [Fact]
    public void Resolve_DropsUnlevelledPartsWhenThePrefabAuthoredLevels()
    {
        var set = new PrefabPartSet<string>();
        set.Add("cooler", "Editor", isVehicle: false, "marker");
        set.Add("cooler", "Model_0", isVehicle: false, "near");

        (Dictionary<string, List<string>> near, _) = set.Resolve();

        Assert.Equal(new[] { "near" }, near["cooler"]);
    }

    [Fact]
    public void Resolve_DrawsAVehiclesAlwaysPartsAtBothLevels()
    {
        var set = new PrefabPartSet<string>();
        set.Add("truck", "Model_0", isVehicle: true, "body");
        set.Add("truck", "Model_1", isVehicle: true, "body-far");
        set.Add("truck", "Seat_0", isVehicle: true, "seat");

        (Dictionary<string, List<string>> near, Dictionary<string, List<string>> far) = set.Resolve();

        Assert.Equal(new[] { "body", "seat" }, near["truck"]);
        Assert.Equal(new[] { "body-far", "seat" }, far["truck"]);
    }

    // A vehicle with no Model_<n> chain has no lower level to switch to: one made of nothing but its seats
    // and lights would replace the whole vehicle at distance.
    [Fact]
    public void Resolve_GivesNoLowerLevelToAVehicleWithoutOne()
    {
        var set = new PrefabPartSet<string>();
        set.Add("cart", "Seat_0", isVehicle: true, "seat");
        set.Add("cart", "Headlights", isVehicle: true, "lights");

        (Dictionary<string, List<string>> near, Dictionary<string, List<string>> far) = set.Resolve();

        Assert.Equal(new[] { "seat", "lights" }, near["cart"]);
        Assert.Empty(far);
    }

    [Fact]
    public void Resolve_NeverDrawsAnExcludedPart()
    {
        var set = new PrefabPartSet<string>();
        set.Add("truck", "DepthMask", isVehicle: true, "mask");
        set.Add("truck", "Model_2", isVehicle: true, "too-coarse");
        set.Add("truck", "Model_0", isVehicle: true, "body");

        (Dictionary<string, List<string>> near, _) = set.Resolve();

        Assert.Equal(new[] { "body" }, near["truck"]);
    }

    // ...and an excluded part is not a fallback candidate either: a prefab left with nothing else must come
    // out empty rather than draw a depth mask as if it were the model.
    [Fact]
    public void Resolve_LeavesOutAPrefabWithOnlyExcludedParts()
    {
        var set = new PrefabPartSet<string>();
        set.Add("truck", "DepthMask", isVehicle: true, "mask");

        (Dictionary<string, List<string>> near, Dictionary<string, List<string>> far) = set.Resolve();

        Assert.Empty(near);
        Assert.Empty(far);
    }

    // A prefab whose only renderers are lower levels still has to draw something.
    [Fact]
    public void Resolve_StandsInForAPrefabThatHasOnlyALowerLevel()
    {
        var set = new PrefabPartSet<string>();
        set.Add("sign", "Model_1", isVehicle: false, "far");

        (Dictionary<string, List<string>> near, Dictionary<string, List<string>> far) = set.Resolve();

        Assert.Equal(new[] { "far" }, near["sign"]);
        Assert.Empty(far);
    }

    // Each collider kind reads a different subset of Unity's fields, and the shape builder trusts the rest
    // to be zero: a capsule that carried a leftover Size would build as a box-sized capsule.
    [Fact]
    public void ColliderPart_CarriesOnlyTheFieldsItsKindUses()
    {
        var pose = new Transform3D(Basis.Identity, new Vector3(1, 2, 3));

        ColliderPart box = ColliderPart.Box(pose, new Vector3(0, 1, 0), new Vector3(2, 3, 4));
        Assert.Equal(EColliderKind.Box, box.Kind);
        Assert.Equal(pose, box.LocalToRoot);
        Assert.Equal(new Vector3(0, 1, 0), box.Center);
        Assert.Equal(new Vector3(2, 3, 4), box.Size);
        Assert.Equal(0f, box.Radius);

        ColliderPart sphere = ColliderPart.Sphere(pose, new Vector3(0, 1, 0), 2.5f);
        Assert.Equal(EColliderKind.Sphere, sphere.Kind);
        Assert.Equal(2.5f, sphere.Radius);
        Assert.Equal(Vector3.Zero, sphere.Size);

        ColliderPart capsule = ColliderPart.Capsule(pose, new Vector3(0, 1, 0), 0.5f, 2f, direction: 1);
        Assert.Equal(EColliderKind.Capsule, capsule.Kind);
        Assert.Equal(0.5f, capsule.Radius);
        Assert.Equal(2f, capsule.Height);
        Assert.Equal(1, capsule.Direction);
        Assert.Equal(Vector3.Zero, capsule.Size);

        ColliderPart mesh = ColliderPart.Mesh(pose, meshId: 42);
        Assert.Equal(EColliderKind.Mesh, mesh.Kind);
        Assert.Equal(42, mesh.MeshId);
        Assert.Equal(Vector3.Zero, mesh.Center);
        Assert.Equal(0f, mesh.Radius);
    }

    [Fact]
    public void MeshPart_CarriesItsMeshMaterialsAndPose()
    {
        var pose = new Transform3D(Basis.Identity, new Vector3(0, 1, 0));
        var part = new MeshPart(7, new List<long> { 11, 12 }, pose);

        Assert.Equal(7, part.MeshId);
        Assert.Equal(new long[] { 11, 12 }, part.Materials);
        Assert.Equal(pose, part.LocalToRoot);
    }

    [Fact]
    public void Resolve_KeepsPrefabsApart()
    {
        var set = new PrefabPartSet<string>();
        set.Add("a", "Model_0", isVehicle: false, "a0");
        set.Add("b", "Alive", isVehicle: false, "b0");

        (Dictionary<string, List<string>> near, _) = set.Resolve();

        Assert.Equal(new[] { "a0" }, near["a"]);
        Assert.Equal(new[] { "b0" }, near["b"]);
    }
}
