using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class RoadAssetTests
{
    private static RoadAsset Parse(string body)
    {
        Assert.True(RoadAsset.TryParse(DatParser.Parse(body), out RoadAsset? asset));
        return asset;
    }

    private const string PeiTrail = """
        GUID 1524cf9f0cf94390881a3e342edfe791
        Type Road

        Width 8
        Depth 0.8
        OffsetAlongNormal -0.1

        TexturePath Roads/PEI_Trail.png

        VanillaPhysicsMaterial Gravel_Static
        """;

    [Fact]
    public void Parses_TheShippedShapeOfARoadAsset()
    {
        RoadAsset trail = Parse(PeiTrail);

        Assert.Equal(Guid.Parse("1524cf9f0cf94390881a3e342edfe791"), trail.Guid);
        Assert.Equal(8f, trail.Width);
        Assert.Equal(0.8f, trail.Depth);
        Assert.Equal(-0.1f, trail.OffsetAlongNormal);
        Assert.Equal("Roads/PEI_Trail.png", trail.TexturePath);
        Assert.Equal("Gravel_Static", trail.VanillaPhysicsMaterial);
        Assert.False(trail.IsConcrete);
    }

    [Fact]
    public void RepeatDistanceScale_DefaultsToOne()
    {
        // ParseFloat("RepeatDistanceScale", 1.0f): six of the sixteen shipped assets omit it, and a zero
        // default would put the whole texture into every metre of road.
        Assert.Equal(1f, Parse(PeiTrail).RepeatDistanceScale);
        Assert.Equal(4f, Parse(PeiTrail + "\nRepeatDistanceScale 4\n").RepeatDistanceScale);
    }

    [Fact]
    public void TryParse_RejectsAnythingThatIsNotARoad()
    {
        // Bundles/Assets holds landscapes, material palettes, weather and songs in the same tree. A road
        // placement resolving onto one of those would hand a width and a depth to something with neither.
        Assert.False(RoadAsset.TryParse(
            DatParser.Parse($"GUID {Guid.NewGuid():N}\nType Landscape_Material\nWidth 8\n"), out _));
        Assert.False(RoadAsset.TryParse(DatParser.Parse("Type Road\nWidth 8\n"), out _)); // no GUID
    }

    [Fact]
    public void ToMaterialConfig_HalvesWidthAndDepthIntoTheLegacyTablesUnits()
    {
        // The trap this port has to get right, and a silent factor of two if it does not. Roads.dat's
        // width and depth are ALREADY halved — Road.cs:774 assigns them straight to halfWidth and
        // halfVerticalSize — while a RoadAsset states the full sizes and Road.cs:758 halves them on the
        // way in. RoadMesh consumes the legacy units, so the asset is converted rather than the reverse.
        RoadMaterialConfig config = Parse(PeiTrail).ToMaterialConfig();

        Assert.Equal(4f, config.Width);    // halfWidth = Width * 0.5
        Assert.Equal(0.4f, config.Depth);  // halfVerticalSize = Depth * 0.5, so verticalSize is 0.8 again
        Assert.Equal(-0.1f, config.Offset);
        Assert.False(config.IsConcrete);
    }

    [Fact]
    public void InverseTextureRepeatDistance_IsWidthTimesAspectTimesScale()
    {
        // Road.cs:802 — `Width * (texture.height / texture.width) * RepeatDistanceScale`, inverted.
        RoadAsset trail = Parse(PeiTrail + "\nRepeatDistanceScale 4\n");

        // 8 * (512/256) * 4 = 64 metres per tile.
        Assert.Equal(1f / 64f, trail.InverseTextureRepeatDistance(256, 512), 6);
        // Square texture: aspect drops out, so 8 * 1 * 4 = 32.
        Assert.Equal(1f / 32f, trail.InverseTextureRepeatDistance(512, 512), 6);
        // No texture to ask: the source falls back to 1, not to a divide by zero.
        Assert.Equal(1f, trail.InverseTextureRepeatDistance(0, 0));
        Assert.Equal(1f, Parse(PeiTrail.Replace("Width 8", "Width 0")).InverseTextureRepeatDistance(256, 512));
    }

    [Fact]
    public void IsConcrete_NarrowsThePhysicsMaterialToTheLegacyToggle()
    {
        Assert.True(Parse(PeiTrail.Replace("Gravel_Static", "Concrete_Static")).IsConcrete);
        Assert.False(Parse(PeiTrail).IsConcrete);
    }

    [Fact]
    public void ScanDirectory_IndexesRoadsByGuidAndIgnoresTheRest()
    {
        using var dir = new TempDir();
        dir.Write(Path.Combine("Roads", "Trail", "Trail.asset"), PeiTrail);
        dir.Write(Path.Combine("Weather", "Rain", "Rain.asset"),
            $"GUID {Guid.NewGuid():N}\nType Weather\n");
        // Road assets are ".asset"; a stray .dat in the same tree is not one.
        dir.Write(Path.Combine("Roads", "Trail", "English.dat"), "Name Trail\n");

        var db = new RoadAssetDatabase();
        db.ScanDirectory(dir.Path);

        Assert.Equal(1, db.Count);
        Assert.NotNull(db.ResolveByGuid(Guid.Parse("1524cf9f0cf94390881a3e342edfe791")));
        Assert.Null(db.ResolveByGuid(Guid.NewGuid()));
        Assert.Null(db.ResolveByGuid(Guid.Empty));
    }

    [Fact]
    public void ScanDirectory_MissingRootIsNotFatal()
    {
        var db = new RoadAssetDatabase();
        db.ScanDirectory(Path.Combine(Path.GetTempPath(), "no-such-assets-dir"));
        db.ScanDirectory("");
        Assert.Equal(0, db.Count);
    }

    // --- Against the real content ---

    [RealDataFact]
    public void RealInstall_CarriesTheSixteenShippedRoadAssets()
    {
        RoadAssetDatabase db = RoadAssetDatabase.ScanSources(ContentSource.Discover(GameData.Install!));

        // Bundles/Assets/Roads: Asphalt_16m/_32m, the lane variants, the trails, the train tracks.
        Assert.Equal(16, db.Count);

        // PEI_Trail, read straight off the shipped file.
        RoadAsset trail = db.ResolveByGuid(Guid.Parse("1524cf9f0cf94390881a3e342edfe791"))!;
        Assert.NotNull(trail);
        Assert.Equal(8f, trail.Width);
        Assert.Equal(0.8f, trail.Depth);
        Assert.Equal(-0.1f, trail.OffsetAlongNormal);
        Assert.False(trail.IsConcrete);

        RoadAsset asphalt = db.ResolveByGuid(Guid.Parse("4dc8d743e8b34862a7e50328d52b73f5"))!;
        Assert.NotNull(asphalt);
        Assert.Equal(16f, asphalt.Width);
        Assert.Equal(-0.2f, asphalt.OffsetAlongNormal);
        Assert.True(asphalt.IsConcrete);

        // Every one of them is usable: a road that resolves to a zero width would build no mesh at all.
        Assert.All(new List<Guid> { trail.Guid, asphalt.Guid }, g => Assert.NotNull(db.ResolveByGuid(g)));
    }

    [RealDataFact(Map = "PEI")]
    public void PeiStillUsesTheLegacyTable()
    {
        // All 23 of PEI's roads carry an empty GUID, so nothing shipped changes shape — the asset path is
        // for maps that migrated. This is the guard that says so out loud.
        List<PlacedRoad> roads = LevelRoads.LoadPaths(
            Path.Combine(GameData.Map("PEI")!, "Environment", "Paths.dat"));

        Assert.Equal(23, roads.Count);
        Assert.All(roads, r => Assert.Equal(Guid.Empty, r.RoadAssetGuid));
    }
}
