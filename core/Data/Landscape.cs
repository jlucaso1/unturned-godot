using Godot;

namespace UnturnedGodot.Data;

// Ports Unturned's Framework/Landscapes/Landscape.cs terrain math.
public static class Landscape
{
    public const float TILE_SIZE = 1024f;
    public const float TILE_HEIGHT = 2048f;
    public const int HEIGHTMAP_RESOLUTION = 257;
    public const int HEIGHTMAP_RESOLUTION_MINUS_ONE = 256;
    public const int SPLATMAP_RESOLUTION = 256;

    // Landscape.cs:35 — "Heightmap resolution minus one". One hole flag per heightmap CELL rather than
    // per sample, which is what makes a hole a 4 m quad of terrain that is simply not there.
    public const int HOLES_RESOLUTION = 256;

    // Mirrors Landscape.getWorldPosition: heightmap indices are transposed vs world
    // axes (hx->Z, hy->X), so keep the swap to match Unturned's placement exactly.
    public static Vector3 GetWorldPosition(int tileX, int tileY, int hx, int hy, float heightNormalized)
    {
        float worldX = tileX * TILE_SIZE + hy / (float)HEIGHTMAP_RESOLUTION_MINUS_ONE * TILE_SIZE;
        float worldY = -TILE_HEIGHT / 2f + heightNormalized * TILE_HEIGHT;
        float worldZ = tileY * TILE_SIZE + hx / (float)HEIGHTMAP_RESOLUTION_MINUS_ONE * TILE_SIZE;
        return new Vector3(worldX, worldY, worldZ);
    }

    // Unity is left-handed, Godot right-handed; negating Z keeps terrain and objects aligned.
    public static Vector3 UnityToGodot(Vector3 unity) => new(unity.X, unity.Y, -unity.Z);
}
