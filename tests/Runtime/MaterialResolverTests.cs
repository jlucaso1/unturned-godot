using System.Collections.Generic;
using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Reading a submesh's material out of the game's own bundle: flat colour, blend mode, the Standard
// shader's surface response, the shader's authored culling, and the texture KEY (without touching the
// pixels, which live in a stream this pass does not read).
//
// The defaults are the part with teeth. A material that cannot be resolved has to come back as plain
// opaque white with back-face culling — the shape of an ordinary surface — because that is what a mesh
// wearing it will be drawn as. Get the fallback wrong and the failure is not an error but a world full of
// subtly wrong surfaces: transparent walls, inside-out geometry, black props.
//
// Needs the game: this reads real Unity material objects through their type trees, and a synthetic one
// this reader accepts would prove nothing about the reader.
public class MaterialResolverTests : TestClass
{
    public MaterialResolverTests(Node testScene) : base(testScene) { }

    // An unresolvable submesh is an ordinary opaque surface, not a hole. Every field of that fallback
    // matters, because a mesh gets drawn with it either way.
    [Test]
    public void AnUnresolvableSubmeshFallsBackToAnOrdinarySurface()
    {
        if (!Ready(out MaterialResolver resolver))
            return;

        (Color color, string texKey, UnityMaterial.Blend blend, long texId, float metallic,
            float smoothness, EShaderCull cull) = resolver.Resolve(0, null, new List<long>());

        Assert.Equal(Colors.White, color);   // not black: an unlit prop is invisible against shadow
        Assert.Empty(texKey);
        Assert.Equal(UnityMaterial.Blend.Opaque, blend); // not transparent: a see-through wall is worse
        Assert.Equal(0, texId);
        Assert.Equal(0f, metallic);
        Assert.Equal(0f, smoothness);
        Assert.Equal(EShaderCull.Back, cull); // not off: inside-out geometry reads as corruption
    }

    // A palette nobody has is null rather than an empty one, so the caller can tell "this asset has no
    // palette" from "this asset's palette is empty" — they resolve differently.
    [Test]
    public void AnUnknownPaletteIsNullRatherThanEmpty()
    {
        if (!Ready(out MaterialResolver resolver))
            return;

        Assert.Null(resolver.PaletteFor(System.Guid.NewGuid()));
    }

    // A submesh index past the renderer's material list resolves to the fallback rather than throwing.
    // Meshes and their material lists disagree in real data — a submesh count that outruns the list is
    // something the extraction meets, not something it can refuse.
    [Test]
    public void ASubmeshPastTheMaterialListIsSurvivable()
    {
        if (!Ready(out MaterialResolver resolver))
            return;

        (Color color, _, UnityMaterial.Blend blend, _, _, _, EShaderCull cull) =
            resolver.Resolve(99, null, new List<long> { 0 });

        Assert.Equal(Colors.White, color);
        Assert.Equal(UnityMaterial.Blend.Opaque, blend);
        Assert.Equal(EShaderCull.Back, cull);
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static bool Ready(out MaterialResolver resolver)
    {
        resolver = null!;
        string? install = UnturnedInstall.Find();
        if (!Available("an Unturned install", install))
            return false;

        string? bundle = UnturnedInstall.FindMasterBundle(install!);
        if (!Available("the core masterbundle", bundle))
            return false;

        PrefabGraph graph = PrefabGraph.Read(ModelExtractor.ReadMasterbundleFile(bundle!));
        resolver = MaterialResolver.Read(graph, Path.Combine(install!, "Bundles", "Objects"), "core");
        return true;
    }

    private static bool Available(string what, string? path)
    {
        if (path != null)
            return true;
        if (System.Environment.GetEnvironmentVariable("UG_REQUIRE_REAL_DATA") == "1")
        {
            throw new InvalidDataException(
                $"UG_REQUIRE_REAL_DATA=1 but {what} is not present; this run exists to prove these tests execute");
        }

        Log.Print($"[runtime-tests] skipping: {what} is not present "
            + "(set UNTURNED_PATH or run ./scripts/fetch-game-data.sh)");
        return false;
    }
}
