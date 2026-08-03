using System.Collections.Generic;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

public class MaterialAliasPlanTests
{
    // The rest of the material state, standing in for (colour, blend, metallic, smoothness, cull).
    private const string Matte = "matte";
    private const string Glass = "glass";

    [Fact]
    public void KeysHoldingTheSameTextureShareOneMaterial_WhenTheRestOfTheStateMatches()
    {
        var surfaces = new List<(string, string)>
        {
            ("core_1a", Matte), ("core_2b", Matte), ("core_1a", Matte), ("core_2b", Glass),
        };
        var identities = new Dictionary<string, string> { ["core_1a"] = "HASH", ["core_2b"] = "HASH" };

        int[] canonical = MaterialAliasPlan.Canonical(surfaces, identities);

        // The first three are one material: same pixels, same rest-of-state. The glass one is not — a
        // shared texture never merges materials that differ anywhere else.
        Assert.Equal(new[] { 0, 0, 0, 3 }, canonical);
        Assert.Equal((2, 2), MaterialAliasPlan.Cost(canonical));
    }

    [Fact]
    public void AnUnresolvedTextureNeverMergesTwoSurfaces()
    {
        var surfaces = new List<(string, string)>
        {
            ("core_1a", Matte), ("core_2b", Matte), ("core_2b", Matte), ("", Matte), ("", Matte),
        };

        // The cold-load state: no identity is known, so every distinct key stays its own material — and an
        // empty value is treated as "still unknown", not as an identity every unresolved key shares.
        var unknown = new Dictionary<string, string> { ["core_1a"] = "", ["core_2b"] = "" };
        int[] cold = MaterialAliasPlan.Canonical(surfaces, unknown);
        Assert.Equal(new[] { 0, 1, 1, 3, 3 }, cold);
        Assert.Equal(cold, MaterialAliasPlan.Canonical(surfaces, new Dictionary<string, string>()));

        // Resolving them is what folds the two keys together; the untextured surfaces are unaffected.
        int[] warm = MaterialAliasPlan.Canonical(surfaces,
            new Dictionary<string, string> { ["core_1a"] = "HASH", ["core_2b"] = "HASH" });
        Assert.Equal(new[] { 0, 0, 0, 3, 3 }, warm);
        Assert.Equal((3, 2), MaterialAliasPlan.Cost(warm));
    }

    [Fact]
    public void TheElectedSurfaceIsTheFirstOfItsGroup_SoTheSameInputsAlwaysMergeTheSameWay()
    {
        var surfaces = new List<(string, string)>
        {
            ("b", Matte), ("a", Matte), ("c", Matte), ("a", Matte),
        };
        var identities = new Dictionary<string, string>
        {
            ["a"] = "ONE",
            ["b"] = "TWO",
            ["c"] = "ONE",
        };

        int[] canonical = MaterialAliasPlan.Canonical(surfaces, identities);

        Assert.Equal(new[] { 0, 1, 1, 1 }, canonical);
        Assert.Equal(canonical, MaterialAliasPlan.Canonical(surfaces, identities));
        // Every elected surface elects itself, so following the plan once is enough.
        foreach (int elected in canonical)
            Assert.Equal(elected, canonical[elected]);
    }

    [Fact]
    public void NothingToMergeCostsNothing()
    {
        int[] canonical = MaterialAliasPlan.Canonical(
            new List<(string, string)>(), new Dictionary<string, string>());
        Assert.Empty(canonical);
        Assert.Equal((0, 0), MaterialAliasPlan.Cost(canonical));
    }
}
