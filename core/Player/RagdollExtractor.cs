using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Unity;

namespace UnturnedGodot.Player;

// Reads Characters/Ragdoll_Zombie out of the game's resources.assets: which bones carry a body, what each
// collides as, and how the joints between them are limited.
//
// The prefab is a Unity ragdoll — a Rigidbody and a collider per bone, tied by CharacterJoints — and every
// number that makes a corpse fall convincingly is in it. Authoring them here instead would be inventing a
// character's proportions and joint limits from nothing.
public static class RagdollExtractor
{
    // Unity class ids, as they appear in the object table.
    private const int GameObjectClassId = 1;
    private const int TransformClassId = 4;
    private const int RigidbodyClassId = 54;
    private const int BoxColliderClassId = 65;
    private const int CharacterJointClassId = 144;

    public const string ZombiePrefab = "Ragdoll_Zombie";
    public const string PlayerPrefab = "Ragdoll_Player";

    // The definition for one prefab, or null when the install cannot be read or the prefab is not in it.
    // Null is a session with no skeletal ragdoll, which falls back to tumbling the body as one piece.
    //
    // `classTypeTrees` is asked for the masterbundle's per-class type trees. It is a parameter rather than
    // a call because the game's implementation caches them under `user://`, and resolving that path needs
    // the engine — the read itself, and everything below it, does not.
    public static RagdollDefinition? Read(string unturnedPath, string prefabName,
        Func<string, IReadOnlyDictionary<int, List<TypeTreeNode>>> classTypeTrees)
    {
        ArgumentNullException.ThrowIfNull(unturnedPath);
        ArgumentNullException.ThrowIfNull(prefabName);
        ArgumentNullException.ThrowIfNull(classTypeTrees);

        string? assetsPath = UnturnedInstall.FindDataFile(unturnedPath, "resources.assets");
        string? bundlePath = UnturnedInstall.FindMasterBundle(unturnedPath);
        if (assetsPath == null || bundlePath == null)
            return null;

        try
        {
            // resources.assets ships with its type trees stripped, so it is decoded with the ones
            // gathered from the masterbundle — the same thing CharacterModel does to read the rigs.
            IReadOnlyDictionary<int, List<TypeTreeNode>> trees = classTypeTrees(bundlePath);
            return Read(SerializedFile.Read(File.ReadAllBytes(assetsPath), trees), prefabName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    // The same, from an already-decoded file. Separate so a caller that has one open — and reading this
    // file costs ~100 ms and tens of megabytes of transient heap — does not open it a second time.
    public static RagdollDefinition? Read(SerializedFile file, string prefabName)
    {
        ArgumentNullException.ThrowIfNull(file);

        var byId = new Dictionary<long, SerializedObject>();
        foreach (SerializedObject o in file.Objects)
            byId[o.PathId] = o;

        Dictionary<string, object> Fields(long id) =>
            TypeTreeReader.Read(byId[id].TypeTree, file.ReaderFor(byId[id]));

        // Every GameObject's name and its components, and the transform tree, so the prefab's own subtree
        // can be walked rather than the whole file scanned for loose bodies.
        var names = new Dictionary<long, string>();
        var componentsOf = new Dictionary<long, List<long>>();
        foreach (SerializedObject o in file.Objects)
        {
            if (o.ClassId != GameObjectClassId)
                continue;
            Dictionary<string, object> go = Fields(o.PathId);
            names[o.PathId] = go.TryGetValue("m_Name", out object? n) && n is string s ? s : string.Empty;
            var components = new List<long>();
            if (go.TryGetValue("m_Component", out object? list))
                foreach (object entry in (List<object>)list)
                {
                    var pair = (Dictionary<string, object>)entry;
                    components.Add(Id(pair.TryGetValue("component", out object? c) ? c : pair["second"]));
                }

            componentsOf[o.PathId] = components;
        }

        var childrenOf = new Dictionary<long, List<long>>();
        var gameObjectOf = new Dictionary<long, long>();
        var transformOf = new Dictionary<long, long>();
        foreach (SerializedObject o in file.Objects)
        {
            if (o.ClassId != TransformClassId)
                continue;
            Dictionary<string, object> t = Fields(o.PathId);
            long owner = Id(t["m_GameObject"]);
            gameObjectOf[o.PathId] = owner;
            transformOf[owner] = o.PathId;
            var children = new List<long>();
            foreach (object child in (List<object>)t["m_Children"])
                children.Add(Id(child));
            childrenOf[o.PathId] = children;
        }

        long root = 0;
        foreach ((long id, string name) in names)
            if (string.Equals(name, prefabName, StringComparison.Ordinal))
            {
                root = id;
                break;
            }

        if (root == 0)
            return null;

        // Which GameObject each Rigidbody belongs to, so a joint's m_ConnectedBody can be named.
        var bodyOwner = new Dictionary<long, long>();
        foreach (SerializedObject o in file.Objects)
            if (o.ClassId == RigidbodyClassId)
                bodyOwner[o.PathId] = Id(Fields(o.PathId)["m_GameObject"]);

        var bones = new List<RagdollBone>();
        Walk(root);
        return bones.Count > 0 ? new RagdollDefinition(bones) : null;

        void Walk(long goId)
        {
            float mass = 0f;
            bool hasBody = false;
            Vector3 center = Vector3.Zero;
            Vector3 size = Vector3.Zero;
            string parent = string.Empty;
            float twistLow = 0f, twistHigh = 0f, swing1 = 0f, swing2 = 0f;

            foreach (long componentId in componentsOf.GetValueOrDefault(goId, new List<long>()))
            {
                if (!byId.TryGetValue(componentId, out SerializedObject? component))
                    continue;
                Dictionary<string, object> fields = Fields(componentId);
                switch (component.ClassId)
                {
                    case RigidbodyClassId:
                        hasBody = true;
                        mass = Single(fields["m_Mass"]);
                        break;
                    case BoxColliderClassId:
                        center = Vec(fields["m_Center"]);
                        size = Vec(fields["m_Size"]);
                        break;
                    case CharacterJointClassId:
                        long connected = Id(fields["m_ConnectedBody"]);
                        if (bodyOwner.TryGetValue(connected, out long owner))
                            parent = names.GetValueOrDefault(owner, string.Empty);
                        twistLow = Limit(fields["m_LowTwistLimit"]);
                        twistHigh = Limit(fields["m_HighTwistLimit"]);
                        swing1 = Limit(fields["m_Swing1Limit"]);
                        swing2 = Limit(fields["m_Swing2Limit"]);
                        break;
                }
            }

            if (hasBody && names.GetValueOrDefault(goId, string.Empty) is { Length: > 0 } name)
            {
                bones.Add(new RagdollBone(name, parent, mass, center, size, twistLow, twistHigh,
                    swing1, swing2));
            }

            if (!transformOf.TryGetValue(goId, out long transform))
                return;
            foreach (long child in childrenOf.GetValueOrDefault(transform, new List<long>()))
                if (gameObjectOf.TryGetValue(child, out long childGo))
                    Walk(childGo);
        }
    }

    private static long Id(object pptr) =>
        Convert.ToInt64(((Dictionary<string, object>)pptr)["m_PathID"]);

    private static float Single(object value) => Convert.ToSingle(value);

    private static Vector3 Vec(object value)
    {
        var d = (Dictionary<string, object>)value;
        return new Vector3(Single(d["x"]), Single(d["y"]), Single(d["z"]));
    }

    // A SoftJointLimit's angle. The struct carries a spring and a bounciness beside it, which this port
    // does not model — Godot's cone-twist has a softness of its own and no equivalent for either.
    private static float Limit(object value)
    {
        var d = (Dictionary<string, object>)value;
        return d.TryGetValue("limit", out object? limit) ? Single(limit) : 0f;
    }
}
