using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class ObjectsBuilder
{
    // Collision layer bit for the bodies Unturned's RayMasks.BLOCK_VISION would hit (the LARGE and
    // MEDIUM object layers). Zombie alert raycasts query exactly this bit.
    public const uint VisionBlockerLayer = 1u << 1;

    // MEDIUM furniture (gravestones, benches, beds) collides with the PLAYER but not with zombie
    // movement: the original's navmesh ignores it (BLOCK_NAVMESH rasterizes only the dedicated Nav
    // colliders) and its zombies shove straight through such props — colliding here made ours jam
    // dead-still on a gravestone the route legitimately crosses. Zombie ground/step rays still see
    // it (a zombie standing on a deck must find the deck).
    public const uint MediumFurnitureLayer = 1u << 2;

    // Instances real meshes (grouped per GUID into one MultiMesh each) where available; placed objects
    // without an extracted mesh fall back to colored placeholder boxes.
    //
    // We deliberately do NOT spatially partition these MultiMeshes into a grid (issue #2). Vegetation is
    // the tempting case — the 1694 trees of a type spread ~3 km, so their single MultiMesh AABB never
    // frustum-culls and re-draws ~679k primitives (~20% of the frame, ~0.18 ms) in every view. But
    // splitting the wide types into a grid was swept empirically (Tier-2) and is a large net LOSS: the
    // oblique/overhead views that dominate frame cost see most cells, so it only multiplies draw calls
    // (507 -> 1605 at 512 m cells / 994 at 2048 m) and ~doubles frame time there, while the only view it
    // helps (near-ground "tight") was already the cheapest (~0.5 ms). Median frame time regressed +52-82%.
    // Real vegetation wins need per-instance LOD/impostors, which MultiMesh doesn't do natively.
    public static Node3D Build(IReadOnlyList<PlacedObject> objects, ObjectAssetDatabase db,
        IReadOnlyDictionary<Guid, ArrayMesh> meshLibrary,
        IReadOnlyDictionary<Guid, List<CachedCollider>> colliderLibrary, out int withMesh)
    {
        var root = new Node3D { Name = "Objects" };

        var byMesh = new Dictionary<Guid, List<Transform3D>>();
        var fallback = new List<(Transform3D transform, Color color)>();

        foreach (PlacedObject obj in objects)
        {
            Transform3D transform = ObjectPlacement.ComputeTransform(obj);
            if (obj.Guid != Guid.Empty && meshLibrary.ContainsKey(obj.Guid))
            {
                if (!byMesh.TryGetValue(obj.Guid, out List<Transform3D>? list))
                    byMesh[obj.Guid] = list = new List<Transform3D>();
                list.Add(transform);
            }
            else
            {
                fallback.Add((transform, ObjectColor.ForAsset(db.Resolve(obj.Guid, obj.Id))));
            }
        }

        var collision = new Node3D { Name = "ObjectCollision" };
        withMesh = 0;
        foreach ((Guid guid, List<Transform3D> transforms) in byMesh)
        {
            // No MaterialOverride: the mesh's per-submesh surface materials carry the textures.
            root.AddChild(BuildMultiMesh(meshLibrary[guid], transforms, $"Mesh_{guid:N}"));
            withMesh += transforms.Count;

            // Only LARGE/MEDIUM objects block the player (SMALL objects have their collider stripped in
            // Unturned). Resources (trees/rocks/bushes) have no such gate: ResourceSpawnpoint instantiates
            // the Resource prefab with all of its colliders while the resource is alive — and at map load
            // every resource is alive. (Bushes pass through because their prefab simply has no collider.)
            // Felling/mining swaps a dead resource to its Stump prefab; that's a future damage system.
            // LARGE/MEDIUM bodies also carry the vision-blocker layer bit: RayMasks.BLOCK_VISION is
            // exactly LARGE | MEDIUM, so zombie alert rays must see these and nothing else.
            EObjectType? type = colliderLibrary.ContainsKey(guid) ? db.Resolve(guid, 0)?.Type : null;
            if (colliderLibrary.TryGetValue(guid, out List<CachedCollider>? colliders)
                && type is EObjectType.Large or EObjectType.Medium or EObjectType.Resource)
            {
                uint layer = type switch
                {
                    EObjectType.Resource => 1u,
                    EObjectType.Medium => MediumFurnitureLayer | VisionBlockerLayer,
                    _ => 1u | VisionBlockerLayer, // LARGE: full world collision
                };
                BuildCollision(collision, guid, colliders, transforms, layer);
            }
        }
        root.AddChild(collision);

        if (fallback.Count > 0)
            root.AddChild(BuildFallbackBoxes(fallback));

        return root;
    }

    // One physics body per GUID holding every instance's shapes (no per-instance nodes) so thousands of
    // instances stay light. Primitive shapes are shared and placed by a per-instance transform. Mesh
    // colliders are shared too (geometry baked to root space once, placed per instance) except for SCALED
    // instances, where the world transform is baked into per-instance vertices — scale on a
    // ConcavePolygonShape3D is what trips Godot's physics.
    private static void BuildCollision(Node3D root, Guid guid, List<CachedCollider> colliders,
        List<Transform3D> instances, uint collisionLayer)
    {
        var shapes = new List<Shape3D>();
        var placements = new List<(int, Transform3D)>();

        var primitives = new List<(int index, Transform3D local)>();
        var meshes = new List<CachedCollider>();
        foreach (CachedCollider c in colliders)
        {
            if (c.Kind == EColliderKind.Mesh)
            {
                meshes.Add(c);
            }
            else if (BuildPrimitive(c) is { } s)
            {
                shapes.Add(s.shape);
                primitives.Add((shapes.Count - 1, s.local));
            }
        }

        // Mesh colliders: ~98% of placed instances carry no scale, so bake each collider's geometry to ROOT
        // space once (LocalToRoot + the Z reflection folded into the vertices) and share that one shape
        // across those instances, placed by the instance transform — a plain rotation+translation the physics
        // engine handles natively. Only SCALED instances keep the per-instance world-space bake, since scale
        // on a ConcavePolygonShape3D is what trips Godot's physics. The geometry is identical either way:
        // (instance * A) * v == instance * (A * v).
        var sharedMesh = new int[meshes.Count];
        for (int m = 0; m < meshes.Count; m++)
        {
            sharedMesh[m] = -1;
            if (MeshShape(meshes[m], UnityMath.ReflectZ(meshes[m].LocalToRoot)) is { } shared)
            {
                shapes.Add(shared);
                sharedMesh[m] = shapes.Count - 1;
            }
        }

        foreach (Transform3D instance in instances)
        {
            foreach ((int index, Transform3D local) in primitives)
                placements.Add((index, instance * local));

            Vector3 sc = instance.Basis.Scale;
            bool unscaled = Mathf.IsEqualApprox(sc.X, 1f, 0.001f)
                && Mathf.IsEqualApprox(sc.Y, 1f, 0.001f) && Mathf.IsEqualApprox(sc.Z, 1f, 0.001f);
            for (int m = 0; m < meshes.Count; m++)
            {
                if (unscaled && sharedMesh[m] >= 0)
                    placements.Add((sharedMesh[m], instance));
                else if (MeshShape(meshes[m], instance * UnityMath.ReflectZ(meshes[m].LocalToRoot)) is { } baked)
                {
                    shapes.Add(baked);
                    placements.Add((shapes.Count - 1, Transform3D.Identity));
                }
            }
        }
        if (placements.Count == 0)
            return;

        root.AddChild(new InstancedStaticBody
        {
            Name = $"Col_{guid:N}",
            Shapes = shapes,
            Placements = placements,
            CollisionLayer = collisionLayer,
        });
    }

    // A primitive Unity collider as a Godot shape + its pose relative to the object root (Unity->Godot).
    private static (Shape3D shape, Transform3D local)? BuildPrimitive(CachedCollider c)
        => c.Kind switch
        {
            EColliderKind.Box => (new BoxShape3D { Size = c.Size },
                UnityMath.ReflectZ(c.LocalToRoot.TranslatedLocal(c.Center))),
            EColliderKind.Sphere => (new SphereShape3D { Radius = c.Radius },
                UnityMath.ReflectZ(c.LocalToRoot.TranslatedLocal(c.Center))),
            EColliderKind.Capsule => (new CapsuleShape3D { Radius = c.Radius, Height = c.Height },
                UnityMath.ReflectZ(c.LocalToRoot.TranslatedLocal(c.Center) * DirectionRotation(c.Direction))),
            _ => null,
        };

    // A MeshCollider as a ConcavePolygonShape3D with each triangle vertex baked through the given transform
    // (F-reflected first: negate Z). Callers pass ReflectZ(LocalToRoot) for the shared root-space shape, or
    // instance * ReflectZ(LocalToRoot) for a per-instance world-space bake.
    private static ConcavePolygonShape3D? MeshShape(CachedCollider c, Transform3D toTarget)
    {
        if (c.Indices.Length < 3 || c.Indices.Length % 3 != 0)
            return null;
        var faces = new Vector3[c.Indices.Length];
        for (int i = 0; i < c.Indices.Length; i++)
        {
            int idx = c.Indices[i];
            if (idx < 0 || idx >= c.Vertices.Length)
                return null;
            Vector3 v = c.Vertices[idx];
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))
                return null;
            faces[i] = toTarget * new Vector3(v.X, v.Y, -v.Z); // F: negate Z, then to target space
        }
        return new ConcavePolygonShape3D { Data = faces };
    }

    // Godot's CapsuleShape3D is Y-aligned; orient it to Unity's m_Direction (0=X, 1=Y, 2=Z).
    private static Transform3D DirectionRotation(int direction) => direction switch
    {
        0 => new Transform3D(new Basis(new Quaternion(new Vector3(0, 0, 1), -Mathf.Pi / 2f)), Vector3.Zero),
        2 => new Transform3D(new Basis(new Quaternion(new Vector3(1, 0, 0), Mathf.Pi / 2f)), Vector3.Zero),
        _ => Transform3D.Identity,
    };

    private static MultiMeshInstance3D BuildMultiMesh(Mesh mesh, List<Transform3D> transforms, string name)
    {
        var multimesh = new MultiMesh
        {
            Mesh = mesh,
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            InstanceCount = transforms.Count,
        };
        // One native buffer upload (12 floats per Transform3D: three basis rows each followed by the
        // origin component) instead of a marshaled SetInstanceTransform call per instance.
        var buffer = new float[transforms.Count * 12];
        for (int i = 0; i < transforms.Count; i++)
        {
            Transform3D t = transforms[i];
            int o = i * 12;
            buffer[o + 0] = t.Basis.X.X; buffer[o + 1] = t.Basis.Y.X; buffer[o + 2] = t.Basis.Z.X; buffer[o + 3] = t.Origin.X;
            buffer[o + 4] = t.Basis.X.Y; buffer[o + 5] = t.Basis.Y.Y; buffer[o + 6] = t.Basis.Z.Y; buffer[o + 7] = t.Origin.Y;
            buffer[o + 8] = t.Basis.X.Z; buffer[o + 9] = t.Basis.Y.Z; buffer[o + 10] = t.Basis.Z.Z; buffer[o + 11] = t.Origin.Z;
        }
        multimesh.Buffer = buffer;

        return new MultiMeshInstance3D { Multimesh = multimesh, Name = name };
    }

    private static MultiMeshInstance3D BuildFallbackBoxes(List<(Transform3D transform, Color color)> items)
    {
        var multimesh = new MultiMesh
        {
            Mesh = new BoxMesh { Size = new Vector3(2, 2, 2) },
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            InstanceCount = items.Count,
        };
        for (int i = 0; i < items.Count; i++)
        {
            multimesh.SetInstanceTransform(i, items[i].transform);
            multimesh.SetInstanceColor(i, items[i].color);
        }

        return new MultiMeshInstance3D
        {
            Multimesh = multimesh,
            Name = "ObjectPlaceholders",
            MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true },
        };
    }
}
