namespace UnturnedGodot.Unity;

// The Unity layer each collider in the game's content sits on (SDG.Unturned.LayerMasks / the project's
// TagManager.asset layer table). Unturned's ray masks are built out of these, and the layer travels with
// every collider in the master bundle, so it is the one signal in the shipped data that says what a
// collider is FOR — the same collider shape means different things on different layers.
//
// Only the layers this port has a use for are named. The rest are left out deliberately: a name here is a
// claim that something reads it, and an unread constant is a claim nobody checks.
public static class UnityLayers
{
    // LayerMasks.LADDER. Unturned tags a ladder's climbing volume "Ladder" AND puts it on this layer; the
    // tag is a project-level string table that a built AssetBundle does not carry (a GameObject serializes
    // its tag as an index into it), so the layer is what survives into the content this port reads. In the
    // shipped core bundle the two agree exactly: every collider on layer 25 sits on a GameObject named
    // "Ladder", and there are no others.
    public const int Ladder = 25;

    // A ladder's volume is a TRIGGER in Unity (it is on a layer the player capsule does not collide with
    // either), so it must never become solid collision here: it is only ever raycast against.
    public static bool IsLadder(int layer) => layer == Ladder;
}
