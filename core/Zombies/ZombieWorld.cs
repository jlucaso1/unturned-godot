using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Data;
using UnturnedGodot.Net;

namespace UnturnedGodot.Zombies;

// Loads a level's zombie world — tables, spawnpoints, nav bounds — and generates the population,
// exactly what LevelZombies.load + ZombieManager.generateZombies do when a map starts hosting.
public static class ZombieWorld
{
    public static ZombieSystem? Load(string levelDir, GroundSampler ground, Random random)
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

        var system = new ZombieSystem(tables, bounds, ground, navmesh.Count > 0 ? navmesh : null);
        system.Spawn(spawnpoints, random);
        return system;
    }
}
