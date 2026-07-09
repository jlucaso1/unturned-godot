using System;
using System.Collections.Generic;

namespace UnturnedGodot.Data;

// The terrain-material lookup behind PhysicsTool.GetTerrainMaterialName: from a Unity-space world position,
// find the splatmap cell (SplatmapCoord: x from world Z, y from world X — the same transposition as the
// heightmap) and return the LandscapeMaterialAsset GUID of the highest-weight layer
// (Landscape.getSplatmapHighestWeightLayerIndex).
public sealed class SplatSampler
{
    private readonly Dictionary<(int x, int y), (SplatmapTile tile, Guid[] materials)> _tiles = new();

    public int TileCount => _tiles.Count;

    public void Add(SplatmapTile tile, Guid[] materials) =>
        _tiles[(tile.CoordX, tile.CoordY)] = (tile, materials);

    public bool TryGetDominantMaterial(float unityX, float unityZ, out Guid materialGuid)
    {
        materialGuid = Guid.Empty;
        int tileX = (int)MathF.Floor(unityX / Landscape.TILE_SIZE);
        int tileY = (int)MathF.Floor(unityZ / Landscape.TILE_SIZE);
        if (!_tiles.TryGetValue((tileX, tileY), out (SplatmapTile tile, Guid[] materials) entry))
            return false;

        // SplatmapCoord(tileCoord, worldPosition)
        int sx = Math.Clamp((int)MathF.Floor((unityZ - (tileY * Landscape.TILE_SIZE))
            / Landscape.TILE_SIZE * Landscape.SPLATMAP_RESOLUTION), 0, Landscape.SPLATMAP_RESOLUTION - 1);
        int sy = Math.Clamp((int)MathF.Floor((unityX - (tileX * Landscape.TILE_SIZE))
            / Landscape.TILE_SIZE * Landscape.SPLATMAP_RESOLUTION), 0, Landscape.SPLATMAP_RESOLUTION - 1);

        int best = -1;
        float bestWeight = -1f;
        int baseIndex = SplatmapTile.WeightIndex(sx, sy, 0);
        for (int layer = 0; layer < SplatmapTile.LAYERS; layer++)
        {
            float w = entry.tile.Weights[baseIndex + layer];
            if (w > bestWeight)
            {
                bestWeight = w;
                best = layer;
            }
        }

        if (best >= entry.materials.Length) // LAYERS > 0, so a dominant layer always exists
            return false;
        materialGuid = entry.materials[best];
        return materialGuid != Guid.Empty;
    }
}
