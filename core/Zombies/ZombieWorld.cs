using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Config;
using UnturnedGodot.Data;
using UnturnedGodot.Net;

namespace UnturnedGodot.Zombies;

// Loads a level's zombie world — tables, spawnpoints, nav bounds — and generates the population,
// exactly what LevelZombies.load + ZombieManager.generateZombies do when a map starts hosting.
public static class ZombieWorld
{
    // `difficulties` is the ZombieDifficultyAsset bank scanned out of the content sources'
    // Bundles/Assets — the map's own per-bound and per-table speciality weights. Null falls back to
    // `mode`, which is Provider.modeConfigData; PEI names no difficulty asset at all, so that fallback
    // is the ordinary case rather than a degraded one.
    //
    // `isNighttime` and `isFullMoon` are LightingManager's, sampled once at generation because that is
    // when generateZombieSpeciality reads them. The Dying Light volatiles are night-only, and a full
    // moon makes the whole population hyper.
    public static ZombieSystem? Load(string levelDir, GroundSampler ground, Random random,
        ZombieDifficultyBank? difficulties = null, ModeConfigData? mode = null,
        bool isNighttime = false, bool isFullMoon = false, ClothingArmorDatabase? clothing = null)
    {
        List<ZombieTable> tables =
            LevelZombiesData.LoadTables(Path.Combine(levelDir, "Spawns", "Zombies.dat"));
        List<ZombieSpawnpointData> spawnpoints =
            LevelZombiesData.LoadSpawnpoints(Path.Combine(levelDir, "Spawns", "Animals.dat"));
        List<NavBound> bounds =
            LevelNavigationData.Load(Path.Combine(levelDir, "Environment"));
        if (tables.Count == 0 || spawnpoints.Count == 0 || bounds.Count == 0)
            return null; // the map ships no zombie world

        // The pre-baked navmesh (Navigation_<N>.dat): exact checkNavigation boxes for the brain,
        // triangles for the host's pathfinding regions. Optional — old maps may not ship it.
        List<NavFlag> navmesh = LevelNavmesh.Load(Path.Combine(levelDir, "Environment"));

        // Set before Spawn: every one of these is read by the speciality roll, which runs per zombie
        // inside it.
        var system = new ZombieSystem(tables, bounds, ground, navmesh.Count > 0 ? navmesh : null)
        {
            Difficulties = difficulties,
            ModeConfig = mode ?? ModeConfigData.Normal,
            IsNighttime = isNighttime,
            IsFullMoon = isFullMoon,
            // Only the damage path reads this, not the roll, so it may arrive after Spawn — but it is
            // set here so a host has one place to hand the world its data.
            Clothing = clothing,
        };
        system.Spawn(spawnpoints, random);
        return system;
    }
}
