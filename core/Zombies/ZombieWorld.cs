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
    // `isNighttime` and `isFullMoon` are LightingManager's. The Dying Light volatiles are night-only, so
    // the roll reads the first at generation; the second is the population's LIVE hyper state, which the
    // host keeps pushing as its clock advances (ZombieSystem.IsFullMoon), and what is passed here is
    // merely the value it starts at.
    //
    // `prioritization` is the map's LevelAsset declaration. Defaulted rather than resolved in here
    // because the resolution needs the installed content sources, which only the host has.
    public static ZombieSystem? Load(string levelDir, GroundSampler ground, Random random,
        ZombieDifficultyBank? difficulties = null, ModeConfigData? mode = null,
        bool isNighttime = false, bool isFullMoon = false, ClothingArmorDatabase? clothing = null,
        EZombieDifficultyAssetPrioritization prioritization =
            EZombieDifficultyAssetPrioritization.NavmeshOverridesTable)
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

        ModeConfigData config = mode ?? ModeConfigData.Normal;

        // Set before Spawn: the speciality roll, the population size and the boss cap all read from here
        // and all run per zombie inside it.
        var system = new ZombieSystem(tables, bounds, ground, navmesh.Count > 0 ? navmesh : null)
        {
            Difficulties = difficulties,
            DifficultyPrioritization = prioritization,
            ModeConfig = config,
            // Zombie.askDamage's two mode switches (Zombie.cs:1041-1048). They stay settable on the
            // system because a test pins them directly, but a HOST has exactly one source for them and
            // it is the same config block everything else here comes from — left unset, a HARD server
            // kept NORMAL's stagger.
            CanStun = config.Zombies.CanStun,
            OnlyCriticalStuns = config.Zombies.OnlyCriticalStuns,
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
