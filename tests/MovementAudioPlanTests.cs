using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

// Which bundle owes which audio definition. Two consumers ask this — the cold-load bundle pass, which
// extracts the clips while it is already streaming the bundle, and the player's deferred fallback for
// whatever the pass did not cover — and they only stay out of each other's way while both get the same
// answer: a fallback that named a definition the pass never planned would open a 1.4 GB bundle for it.
public class MovementAudioPlanTests
{
    private const string CoreConfig = """
        Asset_Bundle_Name core.masterbundle
        Asset_Prefix Assets/CoreMasterBundle
        Asset_Bundle_Version 6
        """;

    private const string ModConfig = """
        Asset_Bundle_Name california2.masterbundle
        Asset_Prefix Assets/CaliforniaMasterBundle
        Asset_Bundle_Version 4
        """;

    private const string GrassDat = """
        Metadata
        {
            GUID 1c3a3c6664f14aa486d73528a5d9f92c
            Type SDG.Unturned.PhysicsMaterialAsset, Assembly-CSharp
        }
        Asset
        {
            UnityNames
            [
                Foliage
            ]
            AudioDefs
            {
                BipedLand Effects/Physics/BipedLand/Grass/BipedLand_Grass.asset
                FootstepWalk Effects/Physics/Footstep/Grass_Walk/Footstep_Grass_Walk.asset
            }
        }
        """;

    private const string ModSurfaceDat = """
        Metadata
        {
            GUID 0a78583e84624e1ab275c89b55c2f688
            Type SDG.Unturned.PhysicsMaterialAsset, Assembly-CSharp
        }
        Asset
        {
            UnityNames
            [
                CA_Boardwalk
            ]
            AudioDefs
            {
                FootstepWalk Effects/Physics/Footstep/Boardwalk/Footstep_Boardwalk.asset
            }
        }
        """;

    private static string BuildInstall(TempDir dir)
    {
        string install = Path.Combine(dir.Path, "steamapps", "common", "Unturned");
        dir.Write(Path.Combine("steamapps", "common", "Unturned", "Bundles", "MasterBundle.dat"), CoreConfig);
        dir.Write(Path.Combine("steamapps", "common", "Unturned", "Bundles", "core_linux.masterbundle"),
            new byte[] { 1 });
        Directory.CreateDirectory(Path.Combine(install, "Bundles", "Objects"));
        Directory.CreateDirectory(Path.Combine(install, "Bundles", "Trees"));
        return install;
    }

    private static string AddMod(TempDir dir)
    {
        string item = Path.Combine("steamapps", "workshop", "content", "304930", "1234567890");
        dir.Write(Path.Combine(item, "MasterBundle.dat"), ModConfig);
        dir.Write(Path.Combine(item, "california2_linux.masterbundle"), new byte[] { 1 });
        Directory.CreateDirectory(Path.Combine(dir.Path, item, "Objects"));
        return Path.Combine(dir.Path, item);
    }

    private static PhysicsMaterialAsset Parse(string dat, string directory)
    {
        Assert.True(PhysicsMaterialAsset.TryParse(DatParser.Parse(dat), out PhysicsMaterialAsset? asset));
        asset!.Directory = directory;
        return asset;
    }

    [Fact]
    public void DefinitionsGoToTheBundleOfTheSourceThatDefinesThem()
    {
        using var dir = new TempDir();
        string install = BuildInstall(dir);
        string mod = AddMod(dir);
        IReadOnlyList<ContentSource> sources = ContentSource.Discover(install,
            UnturnedInstall.Platform.Linux);
        Assert.Equal(2, sources.Count);

        var bank = new PhysicsMaterialBank();
        bank.Add(Parse(GrassDat, Path.Combine(install, "Bundles", "Assets", "PhysicsMaterials", "Grass")));
        bank.Add(Parse(ModSurfaceDat, Path.Combine(mod, "Assets", "PhysicsMaterials", "Boardwalk")));

        string core = UnturnedInstall.MasterBundlePath(install);
        Dictionary<string, HashSet<string>> byBundle =
            MovementAudioPlan.DefPathsByBundle(sources, bank, core);

        Assert.Equal(new HashSet<string>
            {
                "Effects/Physics/BipedLand/Grass/BipedLand_Grass.asset",
                "Effects/Physics/Footstep/Grass_Walk/Footstep_Grass_Walk.asset",
            },
            byBundle[core]);
        Assert.Equal(new HashSet<string> { "Effects/Physics/Footstep/Boardwalk/Footstep_Boardwalk.asset" },
            byBundle[Path.Combine(mod, "california2_linux.masterbundle")]);
    }

    // The zombie roars and groans ride along with the game's own pass, so its entry has to exist even on a
    // map whose every surface is modded — an absent key would leave those clips unextracted for good.
    [Fact]
    public void TheGameBundleAlwaysGetsAnEntry()
    {
        using var dir = new TempDir();
        string install = BuildInstall(dir);
        IReadOnlyList<ContentSource> sources = ContentSource.Discover(install,
            UnturnedInstall.Platform.Linux);
        string core = UnturnedInstall.MasterBundlePath(install);

        Dictionary<string, HashSet<string>> byBundle =
            MovementAudioPlan.DefPathsByBundle(sources, new PhysicsMaterialBank(), core);

        Assert.Empty(Assert.Contains(core, (IDictionary<string, HashSet<string>>)byBundle));
    }

    // A material whose own asset names no definition resolves through its fallback chain, and the bundle
    // that owes the clips is the one holding the asset that DEFINES the event, not the one that inherited
    // it. Attributing it to the inheriting mod sent the pass looking in a bundle without those clips.
    [Fact]
    public void InheritedDefinitionsFollowTheAssetThatDefinesThem()
    {
        using var dir = new TempDir();
        string install = BuildInstall(dir);
        string mod = AddMod(dir);
        IReadOnlyList<ContentSource> sources = ContentSource.Discover(install,
            UnturnedInstall.Platform.Linux);

        var bank = new PhysicsMaterialBank();
        bank.Add(Parse(GrassDat, Path.Combine(install, "Bundles", "Assets", "PhysicsMaterials", "Grass")));
        bank.Add(Parse("""
            Metadata
            {
                GUID 51e0b7a03b844929856fc4f72701fca9
                Type SDG.Unturned.PhysicsMaterialAsset, Assembly-CSharp
            }
            Asset
            {
                UnityNames
                [
                    CA_Lawn
                ]
                Fallback "1c3a3c6664f14aa486d73528a5d9f92c" // the game's grass
            }
            """, Path.Combine(mod, "Assets", "PhysicsMaterials", "Lawn")));

        string core = UnturnedInstall.MasterBundlePath(install);
        Dictionary<string, HashSet<string>> byBundle =
            MovementAudioPlan.DefPathsByBundle(sources, bank, core);

        // The mod owes nothing, so its bundle is not in the plan at all: a pass planned for it would
        // decompress a whole workshop bundle to look for definitions that were never packaged in it.
        Assert.DoesNotContain(Path.Combine(mod, "california2_linux.masterbundle"),
            (IDictionary<string, HashSet<string>>)byBundle);
        Assert.Contains("Effects/Physics/Footstep/Grass_Walk/Footstep_Grass_Walk.asset", byBundle[core]);
    }

    // Every event the runtime can resolve has to be planned, or the surface plays silence until something
    // extracts it: the runtime walks the same three keys through the same bank.
    [Fact]
    public void EveryEventKeyTheRuntimeResolvesIsPlanned()
    {
        Assert.Equal(new[] { "FootstepWalk", "FootstepRun", "BipedLand" }, MovementAudioPlan.EventKeys);
    }
}
