using Godot;

namespace UnturnedGodot;

// Where the extracted impact-decal and crosshair textures live.
//
// One line, and it is here rather than beside the planning code because `user://` is an engine path: only
// ProjectSettings knows what it resolves to on this platform. ImpactDecalRequests takes the directory as
// an argument for exactly that reason — everything it does with it is plain file work, and pushing this
// resolution out to the caller is what let the rest of it move to core/.
//
// Mirrors TerrainLayerCache.Directory, which holds the same line for the same reason.
public static class DecalCache
{
    public static string Directory => ProjectSettings.GlobalizePath("user://decal_cache");
}
