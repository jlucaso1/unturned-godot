using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Damage;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests.Damage;

// The data end of the impact sound: the surfaces a punch can land on have to actually name an impact
// definition in the shipped assets, and that definition has to be one the extraction plans for.
//
// This is where a silent punch would come from without anything looking broken. The bank resolves, the
// message arrives, the play call runs — and no clip was ever pulled out of the bundle, because the
// extraction only ever asked for footsteps.
[Trait("Category", "RealData")]
public class ImpactAudioPlanRealTests
{
    private static IReadOnlyList<ContentSource> Sources() => ContentSource.Discover(GameData.Install!);

    private static PhysicsMaterialBank Bank()
    {
        var roots = new List<string>();
        foreach (ContentSource source in Sources())
            roots.Add(Path.Combine(source.AssetsDir, "PhysicsMaterials"));
        return PhysicsMaterialBank.ScanDirectories(roots);
    }

    // The first of MovementAudioPlan.ImpactEventKeys that resolves, which is the order ImpactAudio plays
    // them in.
    private static string? ImpactDefFor(PhysicsMaterialBank bank, string surface)
    {
        foreach (string key in MovementAudioPlan.ImpactEventKeys)
            if (bank.FindAudioDefPath(surface, key) is { } path)
                return path;
        return null;
    }

    // The surfaces a fist meets most: what the props are made of, plus the Flesh a zombie answers with.
    [RealDataFact]
    public void TheSurfacesAPunchLandsOnAllNameAnImpactDefinition()
    {
        PhysicsMaterialBank bank = Bank();

        foreach (string surface in new[]
                 {
                     "Wood_Static", "Metal_Static", "Concrete_Static", "Gravel_Static", "Tile_Static",
                     PunchTargeting.ZombieSurface,
                 })
        {
            Assert.True(ImpactDefFor(bank, surface) != null,
                $"{surface} resolves to no impact definition through "
                + string.Join('/', MovementAudioPlan.ImpactEventKeys));
        }
    }

    // And the extraction has to have asked for those definitions. Planning footsteps alone is exactly the
    // failure this guards: everything downstream would look correct and nothing would be audible.
    [RealDataFact]
    public void TheExtractionPlansTheImpactDefinitionsAsWellAsTheFootsteps()
    {
        PhysicsMaterialBank bank = Bank();
        string core = UnturnedInstall.MasterBundlePath(GameData.Install!);

        Dictionary<string, HashSet<string>> planned =
            MovementAudioPlan.DefPathsByBundle(Sources(), bank, core);

        Assert.True(planned.TryGetValue(core, out HashSet<string>? corePaths));

        string? wood = ImpactDefFor(bank, "Wood_Static");
        Assert.NotNull(wood);
        Assert.Contains(wood!, corePaths!);

        // The footsteps are still planned; the impact keys are an addition, not a replacement.
        if (bank.FindAudioDefPath("Foliage", "FootstepWalk") is { } grass)
            Assert.Contains(grass, corePaths!);
    }
}
