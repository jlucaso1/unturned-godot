namespace UnturnedGodot;

// The names of the scene-tree groups the built world advertises itself under.
//
// A world is assembled by a dozen independent builders and then handed to the tree in pieces — some of
// them minutes apart, because the objects stream in behind a loading screen — so anything that wants to
// reach "the terrain" or "the foliage" later cannot rely on a path, an order or a field somebody
// remembered to wire. `SceneTree.GetFirstNodeInGroup` is the engine's own answer to that: the builder
// says what a node is once, at construction, and the lookup is a dictionary hit whenever it is needed.
//
// Today the dev console is the only consumer. The names are prefixed so they cannot collide with the
// groups gameplay code already uses for its own purposes.
public static class SceneGroups
{
    public const string Terrain = "ug_terrain";
    // What the object build DRAWS — its batch renderer and its placeholder boxes — rather than the root
    // they hang off. Both build paths attach the NPC root to that root, and NPCs have a group of their
    // own; a group on the root would tie the two switches together.
    public const string Objects = "ug_objects";
    // The MultiMeshRidRenderer inside the objects root — the node that owns the batches themselves, and
    // so the only one that can stop drawing one kind of object without hiding the rest.
    public const string ObjectBatches = "ug_object_batches";
    public const string Foliage = "ug_foliage";
    public const string Roads = "ug_roads";
    public const string Water = "ug_water";
    public const string Vehicles = "ug_vehicles";
    public const string Npcs = "ug_npcs";
    public const string Locations = "ug_locations";
    public const string Zombies = "ug_zombies";
    public const string RemotePlayers = "ug_remote_players";
    public const string Lighting = "ug_lighting";
    public const string PauseMenu = "ug_pause_menu";

    // The session owner (NetworkManager), so the console's read-only `net` namespace and the F3 HUD can
    // read what the netcode is costing. Found through a group like everything else here: the session is
    // created during the world build and torn down with it, so nothing may hold a reference across maps.
    public const string Network = "ug_network";
}
