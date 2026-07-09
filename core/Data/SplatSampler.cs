using System;
using System.Collections.Generic;

namespace UnturnedGodot.Data;

// The terrain-material lookup behind PhysicsTool.GetTerrainMaterialName: from a Unity-space world position,
// find the splatmap cell (SplatmapCoord: x from world Z, y from world X — the same transposition as the
// heightmap) and return the LandscapeMaterialAsset GUID of the highest-weight layer
// (Landscape.getSplatmapHighestWeightLayerIndex). Only the argmax is ever needed, so each tile keeps one
// dominant-layer byte per texel (64 KB) instead of the full 8-float weight set (2 MB) — byte/255 is
// monotonic and ties keep the first layer either way, so the answer is bit-identical.
public sealed class SplatSampler
{
    private readonly Dictionary<(int x, int y), (byte[] dominant, Guid[] materials)> _tiles = new();

    public int TileCount => _tiles.Count;

    public void Add(int coordX, int coordY, byte[] dominantLayers, Guid[] materials) =>
        _tiles[(coordX, coordY)] = (dominantLayers, materials);

    public bool TryGetDominantMaterial(float unityX, float unityZ, out Guid materialGuid)
    {
        materialGuid = Guid.Empty;
        int tileX = (int)MathF.Floor(unityX / Landscape.TILE_SIZE);
        int tileY = (int)MathF.Floor(unityZ / Landscape.TILE_SIZE);
        if (!_tiles.TryGetValue((tileX, tileY), out (byte[] dominant, Guid[] materials) entry))
            return false;

        // SplatmapCoord(tileCoord, worldPosition)
        int sx = Math.Clamp((int)MathF.Floor((unityZ - (tileY * Landscape.TILE_SIZE))
            / Landscape.TILE_SIZE * Landscape.SPLATMAP_RESOLUTION), 0, Landscape.SPLATMAP_RESOLUTION - 1);
        int sy = Math.Clamp((int)MathF.Floor((unityX - (tileX * Landscape.TILE_SIZE))
            / Landscape.TILE_SIZE * Landscape.SPLATMAP_RESOLUTION), 0, Landscape.SPLATMAP_RESOLUTION - 1);

        int best = entry.dominant[(sx * Landscape.SPLATMAP_RESOLUTION) + sy];
        if (best >= entry.materials.Length)
            return false;
        materialGuid = entry.materials[best];
        return materialGuid != Guid.Empty;
    }
}
