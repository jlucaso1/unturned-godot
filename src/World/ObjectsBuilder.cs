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
    // MEDIUM object layers). Zombie alert raycasts query exactly this bit. See CollisionLayers for the
    // whole layout — these two names are kept because the call sites read well with them.
    public const uint VisionBlockerLayer = CollisionLayers.VisionBlocker;

    // MEDIUM furniture (gravestones, benches, beds) collides with the PLAYER but not by default with
    // zombie movement: colliding there made ours jam dead-still on a gravestone the route legitimately
    // crosses. ObjectCollisionPolicy promotes the Fences category to World, because a continuous fence
    // is a wall to a character even though Unturned classifies the asset as MEDIUM. Ground/step rays see
    // both kinds.
    public const uint MediumFurnitureLayer = CollisionLayers.MediumFurniture;

    // Instances real meshes (grouped per GUID into one MultiMesh each) where available; placed objects
    // without an extracted mesh fall back to colored placeholder boxes.
    //
    // A map-wide MultiMesh gives every copy of an asset one enormous AABB, preventing Godot from culling
    // or selecting LODs for distant copies independently. Splitting groups into cells is what gives the
    // frustum something to reject. Set UG_OBJECT_CHUNK_METRES=0 to disable for repeatable A/B profiling.
    //
    // The cell size is a trade, not a free win: finer cells shed geometry at eye level, where most of a
    // group is behind the camera or beyond it, and cost draw calls in views that take in the whole map at
    // once, where nothing can be rejected. Widening it is the crudest way to settle that trade, because
    // it gives up rejection everywhere to save draw calls on the groups too thin to be worth splitting;
    // MinCellTriangles is what separates those two cases, so this stays at the size that suits the dense
    // groups and the coarsening handles the rest.
    private static readonly float ObjectChunkMetres = EnvFloat("UG_OBJECT_CHUNK_METRES", 1024f, 0f, 8192f);
    private static readonly long ObjectChunkMinTriangles = EnvLong("UG_OBJECT_CHUNK_MIN_TRIS", 0, 0, long.MaxValue);
    private static readonly bool ObjectChunkRequireSpread = EnvBool("UG_OBJECT_CHUNK_REQUIRE_SPREAD", true);
    // Geometry an average cell must carry for its own draw call to be worth taking. See CellSizeFor.
    // The default sits where the measured cost of the next step up stops being worth it: coarsening this
    // far buys most of the aerial saving that a larger cell size would, for a fraction of its cost at eye
    // level, and going further costs ground geometry about as fast as simply widening the cells would.
    // Zero restores a single fixed cell size for every group, which is the A/B control.
    private static readonly long MinCellTriangles = EnvLong("UG_OBJECT_CELL_MIN_TRIS", 500, 0, long.MaxValue);
    // Ceiling for that coarsening walk. Past the widest map a cell holds every copy of a group, so going
    // further cannot change the partition — this only bounds the loop against degenerate coordinates.
    private const float MaxCellMetres = 65_536f;
    private static readonly bool ChunkSparseObjects = EnvBool("UG_CHUNK_SPARSE_OBJECTS", true);
    private static readonly long SparseChunkMinTriangles =
        EnvLong("UG_SPARSE_OBJECT_MIN_TRIS", 0, 0, long.MaxValue);
    private const int MinChunkedInstances = 8;

    // Physics counterpart to render chunking. A body containing copies spread across an entire large map
    // has a map-sized broadphase AABB, so every local character sweep reaches that body and then its huge
    // compound. Spatial bodies keep the identical child shapes/transforms but give Jolt useful coarse
    // bounds. Zero remains an A/B control. 2048 m retained nearly all of the measured query benefit of
    // 1024 m with ~36% fewer bodies on California2.
    private static readonly float CollisionChunkMetres =
        EnvFloat("UG_COLLISION_CHUNK_METRES", 2048f, 0f, 4096f);
    private const int MinChunkedCollisionShapes = 32;
    // Jolt defaults to 10,240 bodies. Bound the extra bodies introduced by partitioning so there is room
    // for terrain, roads, players and dynamic entities; once this threshold is reached, later GUIDs stay
    // as their original single compound (slower locally, but with no collision dropped).
    private const int MaxObjectCollisionBodies = 8_000;

    // `navigationField`, when given, receives a CPU copy of every shape and placement that lands on
    // CollisionLayers.World — the only geometry a downward navmesh probe can hit. Recording it here rather
    // than re-deriving it later is what lets navmesh reconciliation run off the physics thread without the
    // two worlds being able to drift apart. See CollisionField.
    //
    // `label` names the roots and the log lines. Vehicles come through here too: a spawned vehicle is a
    // GUID and a transform like everything else the map places, so it wants the same per-GUID batching,
    // authored lower levels and placeholder boxes rather than a second renderer beside them. They pass no
    // navigation field: a vehicle builds no static body here, so it contributes nothing a probe could hit.
    public static Node3D Build(IReadOnlyList<PlacedObject> objects, ObjectAssetDatabase db,
        IReadOnlyDictionary<Guid, ArrayMesh> meshLibrary,
        IReadOnlyDictionary<Guid, List<CachedCollider>> colliderLibrary, out int withMesh,
        IReadOnlyDictionary<Guid, ArrayMesh>? lod1Library = null,
        CollisionFieldBuilder? navigationField = null, string label = "Objects")
    {
        var root = new Node3D { Name = label };

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

        var collision = new Node3D { Name = label + "Collision" };
        InstancedStaticBodies? collisionOwner = EnvFlag.IsOn(OS.GetEnvironment("UG_NODE_PHYSICS"), whenUnset: false)
            ? null : new InstancedStaticBodies { Name = label + "Bodies" };
        int collisionBodyCount = 0;
        var ladders = new LadderVolumes { Name = label + "Ladders" };
        MultiMeshRidRenderer? render = EnvFlag.IsOn(OS.GetEnvironment("UG_NODE_MULTIMESH"), whenUnset: false)
            ? null : new MultiMeshRidRenderer { Name = label + "Batches" };
        var collisionShapes = new CollisionShapePool(navigationField);
        withMesh = 0;
        int renderBatches = 0, sparseGroups = 0, sparseExtraBatches = 0;
        if (EnvFlag.IsOn(OS.GetEnvironment("UG_OBJECT_PROFILE"), whenUnset: false))
            PrintObjectCosts(byMesh, meshLibrary, db);
        foreach ((Guid guid, List<Transform3D> transforms) in byMesh)
        {
            // No MaterialOverride: the mesh's per-submesh surface materials carry the textures.
            ArrayMesh renderMesh = meshLibrary[guid];
            // The prefab's own lower level, if it shipped one. AddLevels derives the distance past which
            // it takes over from the batch it is actually given, so chunked cells switch on their own
            // largest placement rather than on the whole map's.
            ArrayMesh? lodMesh = LodEnabled && lod1Library != null
                && lod1Library.TryGetValue(guid, out ArrayMesh? candidate) ? candidate : null;
            // A visibility range is a property of the whole batch, never of the placements inside it: a
            // batch spanning the map is wholly near or wholly far, so whichever level it picks says
            // nothing about the objects the camera is actually looking at — and picking the far level for
            // a batch that reaches the player draws the coarse mesh right in front of them. Groups that
            // have a lower level are cut into cells small enough for the switch to mean something; groups
            // without one keep the coarse cells, which are the ones tuned for large-map culling.
            // LodChunkMetres is an override, so 0 means "no finer cells", not "no cells": dropping to 0
            // here would leave levelled groups as one map-spanning batch — the opposite of the point, and
            // the opposite of what UG_OBJECT_CHUNK_METRES=0 means for everything else.
            float chunkMetres = lodMesh != null && ObjectChunkMetres > 0f && LodChunkMetres > 0f
                ? Mathf.Min(ObjectChunkMetres, LodChunkMetres)
                : ObjectChunkMetres;
            long trianglesPerInstance = TriangleCount(renderMesh);
            long placementTriangles = trianglesPerInstance * transforms.Count;
            bool spread = chunkMetres > 0f && transforms.Count > 1
                && ExceedsCellSpan(transforms, chunkMetres);
            bool sparseWide = ChunkSparseObjects && transforms.Count < MinChunkedInstances && spread
                && placementTriangles >= SparseChunkMinTriangles;
            if (chunkMetres > 0f && (transforms.Count >= MinChunkedInstances || sparseWide)
                && placementTriangles >= ObjectChunkMinTriangles
                && (!ObjectChunkRequireSpread || spread))
            {
                chunkMetres = CellSizeFor(transforms, chunkMetres, trianglesPerInstance);
                Dictionary<(int X, int Z), List<Transform3D>> cells = Cells(transforms, chunkMetres);
                if (sparseWide)
                {
                    sparseGroups++;
                    sparseExtraBatches += cells.Count - 1;
                }
                foreach (((int x, int z), List<Transform3D> inCell) in cells)
                {
                    renderBatches += AddLevels(root, render, renderMesh, lodMesh, inCell);
                }
            }
            else
            {
                renderBatches += AddLevels(root, render, renderMesh, lodMesh, transforms);
            }
            withMesh += transforms.Count;

            // Only LARGE/MEDIUM objects block the player (SMALL objects have their collider stripped in
            // Unturned). Resources (trees/rocks/bushes) have no such gate: ResourceSpawnpoint instantiates
            // the Resource prefab with all of its colliders while the resource is alive — and at map load
            // every resource is alive. (Bushes pass through because their prefab simply has no collider.)
            // Felling/mining swaps a dead resource to its Stump prefab; that's a future damage system.
            // LARGE/MEDIUM bodies also carry the vision-blocker layer bit: RayMasks.BLOCK_VISION is
            // exactly LARGE | MEDIUM, so zombie alert rays must see these and nothing else.
            ObjectAsset? asset = colliderLibrary.ContainsKey(guid) ? db.Resolve(guid, 0) : null;
            EObjectType? type = asset?.Type;
            if (colliderLibrary.TryGetValue(guid, out List<CachedCollider>? colliders))
            {
                // Ladders first, and deliberately outside the type gate below: what Unturned strips from a
                // purely visual object is the collider on its ROOT, and a ladder's climbing volume hangs
                // off a child. BuildCollision skips these, so a ladder is climbable and never solid.
                BuildLadderVolumes(ladders, colliders, transforms);

                if (type is EObjectType.Large or EObjectType.Medium or EObjectType.Resource)
                {
                    uint layer = ObjectCollisionPolicy.PhysicsLayer(asset!);
                    BuildCollision(collision, collisionOwner, ref collisionBodyCount, guid, colliders,
                        transforms, layer, collisionShapes);
                }
            }
        }
        if (ladders.Count > 0)
        {
            Log.Print($"[unturned-godot] {label} ladders: {ladders.Count} climbable volumes");
            collision.AddChild(ladders);
        }
        else
        {
            ladders.Free(); // never entered the tree, so nothing else will
        }
        if (CollisionChunkMetres > 0f && collisionBodyCount > 0)
            Log.Print($"[unturned-godot] {label} collision bodies: {collisionBodyCount} "
                + $"({CollisionChunkMetres:0} m cells, min {MinChunkedCollisionShapes} shapes, "
                + $"{collisionShapes.Shapes.Count} shared shapes, "
                + $"{collisionShapes.PrimitiveAliases} primitive + {collisionShapes.MeshAliases} mesh aliases)");
        if (render != null)
            root.AddChild(render);
        if (collisionOwner != null)
            collision.AddChild(collisionOwner);
        root.AddChild(collision);

        if (fallback.Count > 0)
            root.AddChild(BuildFallbackBoxes(fallback, label));

        if (ObjectChunkMetres > 0f)
            Log.Print($"[unturned-godot] {label} render batches: {renderBatches} " +
                $"({ObjectChunkMetres:0} m cells, min {MinChunkedInstances} instances / " +
                $"{ObjectChunkMinTriangles:N0} placement tris, coarsen below {MinCellTriangles:N0} " +
                $"tris/cell, require spread={ObjectChunkRequireSpread}, " +
                $"sparse-wide >= {SparseChunkMinTriangles:N0} tris: {sparseGroups} groups / " +
                $"+{sparseExtraBatches} batches)");

        return root;
    }

    private static float EnvFloat(string name, float fallback, float min, float max) =>
        float.TryParse(System.Environment.GetEnvironmentVariable(name),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float value)
            ? Math.Clamp(value, min, max)
            : fallback;

    private static long EnvLong(string name, long fallback, long min, long max) =>
        long.TryParse(System.Environment.GetEnvironmentVariable(name), out long value)
            ? Math.Clamp(value, min, max)
            : fallback;

    private static bool EnvBool(string name, bool fallback) =>
        System.Environment.GetEnvironmentVariable(name) switch
        {
            "1" or "true" or "yes" => true,
            "0" or "false" or "no" => false,
            _ => fallback,
        };

    private static long TriangleCount(ArrayMesh mesh)
    {
        long triangles = 0;
        for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
            triangles += mesh.SurfaceGetArrayIndexLen(surface) / 3;
        return triangles;
    }

    // The grid is laid out from the group's own lowest corner, not from the world origin. Anchoring it
    // at the origin makes the partition depend on where the map happens to sit relative to it: a group
    // straddling the origin is cut along it however large the cells are, so it can never fall below four
    // batches — and on a map centred there, that is most of them. Anchoring per group means the cell size
    // alone decides how a group is cut, wherever it lies.
    private static (int X, int Z) CellOf(Vector3 origin, Vector3 anchor, float cellSize) => (
        Mathf.FloorToInt((origin.X - anchor.X) / cellSize),
        Mathf.FloorToInt((origin.Z - anchor.Z) / cellSize));

    private static Vector3 AnchorOf(List<Transform3D> transforms)
    {
        Vector3 anchor = transforms[0].Origin;
        foreach (Transform3D transform in transforms)
            anchor = anchor.Min(transform.Origin);
        return anchor;
    }

    private static Dictionary<(int X, int Z), List<Transform3D>> Cells(
        List<Transform3D> transforms, float cellSize)
    {
        Vector3 anchor = AnchorOf(transforms);
        var cells = new Dictionary<(int X, int Z), List<Transform3D>>();
        foreach (Transform3D transform in transforms)
        {
            (int X, int Z) cell = CellOf(transform.Origin, anchor, cellSize);
            if (!cells.TryGetValue(cell, out List<Transform3D>? inCell))
                cells[cell] = inCell = new List<Transform3D>();
            inCell.Add(transform);
        }
        return cells;
    }

    private static int CellCount(List<Transform3D> transforms, float cellSize)
    {
        Vector3 anchor = AnchorOf(transforms);
        var seen = new HashSet<(int X, int Z)>();
        foreach (Transform3D transform in transforms)
            seen.Add(CellOf(transform.Origin, anchor, cellSize));
        return seen.Count;
    }

    // One cell size cannot suit every group. A split trades a draw call for the chance to reject
    // geometry, so it pays in proportion to how much geometry lands in each cell it creates: cutting a
    // group of heavy copies into cells sheds most of it at eye level, while cutting a group of tiny ones
    // scatters near-empty batches across the map that reject almost nothing and are pure cost in any view
    // that takes the whole map in at once. Both live in the same scene, and the count of cells a fixed
    // size produces also grows with the map, so a single tuned number is either too fine for one map or
    // too coarse for the other.
    //
    // Coarsen per group instead, from the configured size, until an average cell carries enough geometry
    // to earn its draw call. Doubling keeps the grids nested, so the cell count falls monotonically and
    // the walk always terminates — in the limit at the whole group in one cell, which is what a group too
    // light to be worth splitting should be.
    private static float CellSizeFor(List<Transform3D> transforms, float baseMetres,
        long trianglesPerInstance)
    {
        if (MinCellTriangles <= 0 || trianglesPerInstance <= 0)
            return baseMetres;
        float metres = baseMetres;
        while (metres < MaxCellMetres)
        {
            int cells = CellCount(transforms, metres);
            if (cells <= 1 || trianglesPerInstance * transforms.Count / cells >= MinCellTriangles)
                return metres;
            metres *= 2f;
        }
        return metres;
    }

    private static bool ExceedsCellSpan(List<Transform3D> transforms, float cellSize)
    {
        float minX = transforms[0].Origin.X, maxX = minX;
        float minZ = transforms[0].Origin.Z, maxZ = minZ;
        foreach (Transform3D transform in transforms)
        {
            Vector3 p = transform.Origin;
            minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X);
            minZ = MathF.Min(minZ, p.Z); maxZ = MathF.Max(maxZ, p.Z);
            if (maxX - minX > cellSize || maxZ - minZ > cellSize)
                return true;
        }
        return false;
    }

    private static void PrintObjectCosts(Dictionary<Guid, List<Transform3D>> byMesh,
        IReadOnlyDictionary<Guid, ArrayMesh> meshLibrary, ObjectAssetDatabase db)
    {
        var costs = new List<(Guid Guid, int Instances, int Surfaces, long Triangles, float Spread,
            bool CellSpan)>();
        foreach ((Guid guid, List<Transform3D> transforms) in byMesh)
        {
            ArrayMesh mesh = meshLibrary[guid];
            int surfaces = mesh.GetSurfaceCount();
            long trianglesPerInstance = TriangleCount(mesh);

            Vector3 min = transforms[0].Origin;
            Vector3 max = min;
            foreach (Transform3D transform in transforms)
            {
                min = min.Min(transform.Origin);
                max = max.Max(transform.Origin);
            }
            costs.Add((guid, transforms.Count, surfaces, trianglesPerInstance * transforms.Count,
                min.DistanceTo(max), transforms.Count > 1 && ObjectChunkMetres > 0f
                    && ExceedsCellSpan(transforms, ObjectChunkMetres)));
        }
        costs.Sort((a, b) => b.Triangles.CompareTo(a.Triangles));
        Log.Print("[object-profile] top full-detail placement costs:");
        for (int i = 0; i < Math.Min(30, costs.Count); i++)
        {
            var c = costs[i];
            ObjectAsset? asset = db.Resolve(c.Guid, 0);
            string name = asset?.Name ?? System.IO.Path.GetFileName(asset?.Directory) ?? c.Guid.ToString("N");
            Log.Print($"[object-profile] {i + 1,2}. {name}: {c.Triangles:N0} tris, " +
                $"{c.Instances:N0} instances, {c.Surfaces} surfaces, spread {c.Spread:0} m, {c.Guid:N}");
        }
        Log.Print("[object-profile] sparse groups crossing cells:");
        int sparseRank = 0;
        foreach (var c in costs)
        {
            if (c.Instances >= MinChunkedInstances || !c.CellSpan)
                continue;
            sparseRank++;
            Log.Print($"[object-profile] S{sparseRank,2}. {c.Guid:N}: {c.Triangles:N0} tris, "
                + $"{c.Instances} instances, {c.Surfaces} surfaces, spread {c.Spread:0} m");
            if (sparseRank == 30)
                break;
        }
    }

    // One physics body per GUID holding every instance's shapes (no per-instance nodes) so thousands of
    // instances stay light. Primitive shapes are shared and placed by a per-instance transform. Mesh
    // colliders are shared too (geometry baked to root space once, placed per instance) except for SCALED
    // instances, where the world transform is baked into per-instance vertices — scale on a
    // ConcavePolygonShape3D is what trips Godot's physics.
    private sealed class CollisionShapePool
    {
        public readonly List<Shape3D> Shapes = new();
        public readonly Dictionary<(List<CachedCollider>, int, long, long, long), int> Primitives = new();
        public readonly Dictionary<(List<CachedCollider>, int), int> Meshes = new();
        private readonly Dictionary<PrimitiveShapeIdentity, int> _finalPrimitives = new();
        private readonly Dictionary<string, int> _finalMeshes = new(StringComparer.Ordinal);
        private static readonly bool DeduplicateFinal =
            EnvFlag.IsOn(System.Environment.GetEnvironmentVariable("UG_DEDUP_FINAL_SHAPES"), whenUnset: true);
        public int PrimitiveAliases { get; private set; }
        public int MeshAliases { get; private set; }

        // The CPU mirror of this pool. Every shape created for the physics server is recorded there as
        // well, and _mirrored maps a pool index to the field's own — the field may already hold shapes
        // from another builder, so the two tables cannot be assumed to run in lockstep.
        public readonly CollisionFieldBuilder? Field;
        private readonly List<int> _mirrored = new();

        public CollisionShapePool(CollisionFieldBuilder? field) => Field = field;

        public int MirrorOf(int shape) => _mirrored[shape];

        private int Add(Shape3D shape, Vector3[]? faces)
        {
            Shapes.Add(shape);
            if (Field == null)
                return Shapes.Count - 1;
            _mirrored.Add(shape switch
            {
                BoxShape3D box => Field.AddBoxShape(box.Size),
                SphereShape3D sphere => Field.AddSphereShape(sphere.Radius),
                CapsuleShape3D capsule => Field.AddCapsuleShape(capsule.Radius, capsule.Height),
                _ when faces != null => Field.AddMeshShape(faces),
                // A shape the field does not model. Recorded anyway, so the pool index still maps to
                // something, and simply never hit — the confirmation pass is what covers it.
                _ => Field.AddUnsupportedShape(),
            });
            return Shapes.Count - 1;
        }

        public int AddPrimitive(Shape3D shape)
        {
            PrimitiveShapeIdentity identity = shape switch
            {
                BoxShape3D box => CollisionShapeIdentity.Primitive((byte)EColliderKind.Box,
                    box.Size.X, box.Size.Y, box.Size.Z),
                SphereShape3D sphere => CollisionShapeIdentity.Primitive((byte)EColliderKind.Sphere,
                    sphere.Radius),
                CapsuleShape3D capsule => CollisionShapeIdentity.Primitive((byte)EColliderKind.Capsule,
                    capsule.Radius, capsule.Height),
                _ => throw new ArgumentException("Unsupported primitive shape.", nameof(shape)),
            };
            if (DeduplicateFinal && _finalPrimitives.TryGetValue(identity, out int existing))
            {
                PrimitiveAliases++;
                shape.Dispose();
                return existing;
            }
            int added = Add(shape, null);
            if (DeduplicateFinal)
                _finalPrimitives[identity] = added;
            return added;
        }

        public int AddMesh(CachedCollider collider, Transform3D toTarget)
        {
            Vector3[]? faces = MeshFaces(collider, toTarget);
            if (faces == null)
                return -1;
            string identity = CollisionShapeIdentity.Mesh(faces);
            if (DeduplicateFinal && _finalMeshes.TryGetValue(identity, out int existing))
            {
                MeshAliases++;
                return existing;
            }
            int added = Add(new ConcavePolygonShape3D { Data = faces }, faces);
            if (DeduplicateFinal)
                _finalMeshes[identity] = added;
            return added;
        }
    }

    // One body per placed ladder volume, carrying the frame the climb rules read back. Kept out of the
    // batched bodies above on purpose: those share one body per GUID (or per cell), and a ray hit on one
    // of them cannot say WHICH ladder it met, which is the only thing this build is for.
    // Each volume gets its own shape rather than a pooled one: they are a handful of boxes, and the pool's
    // sharing exists to keep thousands of identical props from each holding their own.
    private static void BuildLadderVolumes(LadderVolumes owner, List<CachedCollider> colliders,
        List<Transform3D> instances)
    {
        foreach (CachedCollider c in colliders)
        {
            if (!c.IsLadder || c.Kind == EColliderKind.Mesh)
            {
                // Every ladder volume in the shipped content is a box. One authored as a mesh collider —
                // which only workshop content could produce — would fall out of both paths, since
                // BuildCollision skips ladders entirely, so say so rather than lose it in silence.
                if (c.IsLadder)
                    Log.Print("[unturned-godot] a ladder volume is a mesh collider; it is not climbable");
                continue;
            }

            foreach (Transform3D instance in instances)
            {
                Transform3D world = instance * PrimitiveLocal(c);
                if (IsDegenerate(world.Basis))
                    continue;
                Shape3D? shape = BuildPrimitiveShape(c, world.Basis.Scale);
                if (shape == null)
                    continue;
                // The frame is the collider's GameObject transform — WITHOUT its centre offset, which
                // belongs to the shape and not to the ladder's own position and facing.
                owner.Add(shape, world.Orthonormalized(),
                    (instance * UnityMath.ReflectZ(c.LocalToRoot)).Orthonormalized());
            }
        }
    }

    private static void BuildCollision(Node3D root, InstancedStaticBodies? owner, ref int bodyCount,
        Guid guid, List<CachedCollider> colliders,
        List<Transform3D> instances, uint collisionLayer, CollisionShapePool pool)
    {
        var primitives = new List<(CachedCollider Collider, int Index)>();
        var meshes = new List<(CachedCollider Collider, int Index)>();
        for (int colliderIndex = 0; colliderIndex < colliders.Count; colliderIndex++)
        {
            CachedCollider c = colliders[colliderIndex];
            if (c.IsLadder)
                continue; // a climbing volume, built by BuildLadderVolumes; never solid
            if (c.Kind == EColliderKind.Mesh)
                meshes.Add((c, colliderIndex));
            else if (c.Kind is EColliderKind.Box or EColliderKind.Sphere or EColliderKind.Capsule)
                primitives.Add((c, colliderIndex));
        }

        bool directBuckets = EnvFlag.IsOn(OS.GetEnvironment("UG_DIRECT_COLLISION_BUCKETS"), whenUnset: true);
        bool mayChunk = CollisionChunkMetres > 0f
            && (long)instances.Count * (primitives.Count + meshes.Count) >= MinChunkedCollisionShapes;
        SpatialBuckets<(int Shape, Transform3D Transform)>? buckets = directBuckets && mayChunk
            ? new SpatialBuckets<(int, Transform3D)>(CollisionChunkMetres) : null;
        List<(int Shape, Transform3D Transform)>? directFlat = directBuckets && !mayChunk
            ? new List<(int, Transform3D)>() : null;
        List<(int Shape, Transform3D Transform, Vector3 WorldOrigin)>? legacy = directBuckets
            ? null : new List<(int, Transform3D, Vector3)>();

        // Only the bodies a navmesh probe can hit are worth mirroring: that probe masks
        // CollisionLayers.World, so MEDIUM furniture — which deliberately sits off it — is skipped here
        // exactly as the physics query skips it.
        CollisionFieldBuilder? field =
            (collisionLayer & CollisionLayers.World) != 0 ? pool.Field : null;

        void AddPlacement(int shape, Transform3D transform, Vector3 origin)
        {
            field?.AddInstance(pool.MirrorOf(shape), transform);
            if (buckets != null)
                buckets.Add(origin.X, origin.Z, (shape, transform));
            else if (directFlat != null)
                directFlat.Add((shape, transform));
            else
                legacy!.Add((shape, transform, origin));
        }

        // One shape per (collider, scale) rather than per instance: the scale is baked into the shape, and
        // the overwhelming majority of instances share a scale (usually 1), so this stays a handful of
        // shapes even on a map with a hundred thousand objects. Quantised so floating-point noise in the
        // placement data cannot spawn a near-duplicate shape per instance.
        int ShapeFor(int primitive, Vector3 scale)
        {
            var key = (colliders, primitives[primitive].Index,
                (long)MathF.Round(scale.X * 1000f),
                (long)MathF.Round(scale.Y * 1000f),
                (long)MathF.Round(scale.Z * 1000f));
            if (pool.Primitives.TryGetValue(key, out int existing))
                return existing;
            Shape3D? built = BuildPrimitiveShape(primitives[primitive].Collider, scale);
            if (built == null)
                return -1;
            int added = pool.AddPrimitive(built);
            pool.Primitives[key] = added;
            return added;
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
            var key = (colliders, meshes[m].Index);
            if (pool.Meshes.TryGetValue(key, out int existing))
            {
                sharedMesh[m] = existing;
                continue;
            }
            sharedMesh[m] = -1;
            CachedCollider collider = meshes[m].Collider;
            int shared = pool.AddMesh(collider, UnityMath.ReflectZ(collider.LocalToRoot));
            if (shared >= 0)
            {
                sharedMesh[m] = shared;
                pool.Meshes[key] = sharedMesh[m];
            }
        }

        foreach (Transform3D instance in instances)
        {
            for (int i = 0; i < primitives.Count; i++)
            {
                // Compose first, then split: the scale that matters is the instance's and the collider's
                // own combined. Orthonormalized strips it from the basis while leaving the origin — which
                // already has the scale applied to it — exactly where it belongs.
                Transform3D world = instance * PrimitiveLocal(primitives[i].Collider);
                // A collapsed axis leaves the basis singular: Orthonormalized cannot recover a frame from
                // it, and the shape would enclose no volume anyway. Placed objects do carry these (an
                // author zeroing an axis to hide a piece), so skip rather than hand physics a degenerate
                // transform it has to guess at.
                if (IsDegenerate(world.Basis))
                    continue;
                int index = ShapeFor(i, world.Basis.Scale);
                if (index >= 0)
                    AddPlacement(index, world.Orthonormalized(), world.Origin);
            }

            if (IsDegenerate(instance.Basis))
                continue; // see above: nothing meaningful to collide with

            Vector3 sc = instance.Basis.Scale;
            bool unscaled = Mathf.IsEqualApprox(sc.X, 1f, 0.001f)
                && Mathf.IsEqualApprox(sc.Y, 1f, 0.001f) && Mathf.IsEqualApprox(sc.Z, 1f, 0.001f);
            for (int m = 0; m < meshes.Count; m++)
            {
                if (unscaled && sharedMesh[m] >= 0)
                    AddPlacement(sharedMesh[m], instance, instance.Origin);
                else
                {
                    int baked = pool.AddMesh(meshes[m].Collider,
                        instance * UnityMath.ReflectZ(meshes[m].Collider.LocalToRoot));
                    if (baked >= 0)
                        AddPlacement(baked, Transform3D.Identity, instance.Origin);
                }
            }
        }

        if (directBuckets)
        {
            int count = buckets?.Count ?? directFlat!.Count;
            if (count == 0) return;
            if (buckets != null && count >= MinChunkedCollisionShapes
                && bodyCount + buckets.Groups.Count <= MaxObjectCollisionBodies)
            {
                foreach (((int x, int z), List<(int Shape, Transform3D Transform)> inCell)
                    in buckets.Groups)
                {
                    AddCollisionBody(root, owner, ref bodyCount, $"Col_{guid:N}_{x}_{z}",
                        pool.Shapes, inCell, collisionLayer);
                }
                return;
            }
            AddCollisionBody(root, owner, ref bodyCount, $"Col_{guid:N}", pool.Shapes,
                directFlat ?? buckets!.Flatten(), collisionLayer);
            return;
        }

        List<(int Shape, Transform3D Transform, Vector3 WorldOrigin)> placements = legacy!;
        if (placements.Count == 0) return;

        if (CollisionChunkMetres > 0f && placements.Count >= MinChunkedCollisionShapes)
        {
            var cells = new Dictionary<(int X, int Z), List<(int Shape, Transform3D Transform)>>();
            foreach ((int shape, Transform3D transform, Vector3 origin) in placements)
            {
                var cell = (Mathf.FloorToInt(origin.X / CollisionChunkMetres),
                    Mathf.FloorToInt(origin.Z / CollisionChunkMetres));
                if (!cells.TryGetValue(cell, out List<(int Shape, Transform3D Transform)>? inCell))
                    cells[cell] = inCell = new List<(int, Transform3D)>();
                inCell.Add((shape, transform));
            }
            if (bodyCount + cells.Count <= MaxObjectCollisionBodies)
            {
                foreach (((int x, int z), List<(int Shape, Transform3D Transform)> inCell) in cells)
                    AddCollisionBody(root, owner, ref bodyCount, $"Col_{guid:N}_{x}_{z}",
                        pool.Shapes, inCell, collisionLayer);
                return;
            }
        }

        var bodyPlacements = new List<(int Shape, Transform3D Transform)>(placements.Count);
        foreach ((int shape, Transform3D transform, Vector3 _) in placements)
            bodyPlacements.Add((shape, transform));
        AddCollisionBody(root, owner, ref bodyCount, $"Col_{guid:N}", pool.Shapes,
            bodyPlacements, collisionLayer);
    }

    private static void AddCollisionBody(Node3D root, InstancedStaticBodies? owner, ref int bodyCount,
        string name, IReadOnlyList<Shape3D> shapes,
        IReadOnlyList<(int Shape, Transform3D Transform)> placements, uint collisionLayer)
    {
        bodyCount++;
        if (owner != null)
            owner.Add(name, shapes, placements, collisionLayer);
        else
            root.AddChild(new InstancedStaticBody
            {
                Name = name,
                Shapes = shapes,
                Placements = placements,
                CollisionLayer = collisionLayer,
            });
    }

    // True when any axis has collapsed, so the basis has no inverse and no orientation to extract.
    private static bool IsDegenerate(Basis basis)
    {
        Vector3 scale = basis.Scale;
        const float Epsilon = 1e-4f;
        return MathF.Abs(scale.X) < Epsilon || MathF.Abs(scale.Y) < Epsilon
            || MathF.Abs(scale.Z) < Epsilon;
    }

    // The pose of a primitive collider relative to the object root (Unity->Godot), scale included.
    private static Transform3D PrimitiveLocal(CachedCollider c) => c.Kind == EColliderKind.Capsule
        ? UnityMath.ReflectZ(c.LocalToRoot.TranslatedLocal(c.Center) * DirectionRotation(c.Direction))
        : UnityMath.ReflectZ(c.LocalToRoot.TranslatedLocal(c.Center));

    // A primitive Unity collider as a Godot shape, with `scale` BAKED INTO ITS DIMENSIONS rather than left
    // on the body's transform.
    //
    // Jolt refuses non-uniform scale on spheres and capsules: it silently substitutes the average of the
    // three axes, so a prop scaled (0.75, 0.75, 1) collides as if it were (0.83, 0.83, 0.83) — wrong shape,
    // and a console error per instance. Baking sidesteps that entirely, because the body then carries a
    // plain rotation and translation.
    //
    // The bake follows Unity's own rules for scaled colliders, which exist for the same reason (a sphere
    // cannot be squashed and stay a sphere): a box scales per axis, a sphere takes the largest axis, and a
    // capsule takes the largest of the two axes across its length plus its own axis for the height. So this
    // is not an approximation of Unity — it is what Unity does.
    private static Shape3D? BuildPrimitiveShape(CachedCollider c, Vector3 scale)
    {
        // Abs: an extent is a magnitude, and Godot rejects a negative one outright. Unity carries
        // mirroring in the size instead — a collider baked from a prefab scaled by -1 on an axis arrives
        // with that axis negated — so the sign belongs to the transform and never to the extent.
        Vector3 s = scale.Abs();
        switch (c.Kind)
        {
            case EColliderKind.Box:
                return new BoxShape3D { Size = c.Size.Abs() * s };

            case EColliderKind.Sphere:
                return new SphereShape3D
                {
                    Radius = MathF.Abs(c.Radius) * MathF.Max(s.X, MathF.Max(s.Y, s.Z)),
                };

            case EColliderKind.Capsule:
                {
                    // The scale arrives measured in the basis DirectionRotation already produced, so the
                    // capsule's length is on Y whatever its Unity direction was: each column of that basis
                    // is how far the transform stretches the corresponding post-rotation axis. Switching
                    // on Direction here as well permuted the axes a second time, and an X-directed capsule
                    // took its transverse scale as its height.
                    float radius = MathF.Abs(c.Radius) * MathF.Max(s.X, s.Z);
                    float alongLength = s.Y;
                    return new CapsuleShape3D
                    {
                        Radius = radius,
                        // Godot's capsule height spans the whole shape and may not be shorter than its own
                        // diameter; a mirrored or degenerate prefab can produce both.
                        Height = MathF.Max(MathF.Abs(c.Height) * alongLength, radius * 2f),
                    };
                }

            default:
                return null;
        }
    }

    // A MeshCollider as a ConcavePolygonShape3D with each triangle vertex baked through the given transform
    // (F-reflected first: negate Z). Callers pass ReflectZ(LocalToRoot) for the shared root-space shape, or
    // instance * ReflectZ(LocalToRoot) for a per-instance world-space bake.
    private static Vector3[]? MeshFaces(CachedCollider c, Transform3D toTarget)
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
        return faces;
    }

    // Godot's CapsuleShape3D is Y-aligned; orient it to Unity's m_Direction (0=X, 1=Y, 2=Z).
    private static Transform3D DirectionRotation(int direction) => direction switch
    {
        0 => new Transform3D(new Basis(new Quaternion(new Vector3(0, 0, 1), -Mathf.Pi / 2f)), Vector3.Zero),
        2 => new Transform3D(new Basis(new Quaternion(new Vector3(1, 0, 0), Mathf.Pi / 2f)), Vector3.Zero),
        _ => Transform3D.Identity,
    };

    // A batch's placement bounds: the centre its transforms are rebased around, and how far the furthest
    // placement sits from that centre.
    private readonly record struct BatchBounds(Vector3 Centre, float Radius);

    private static BatchBounds BoundsOf(List<Transform3D> transforms)
    {
        Vector3 min = transforms[0].Origin, max = min;
        foreach (Transform3D t in transforms)
        {
            min = new Vector3(Mathf.Min(min.X, t.Origin.X), Mathf.Min(min.Y, t.Origin.Y), Mathf.Min(min.Z, t.Origin.Z));
            max = new Vector3(Mathf.Max(max.X, t.Origin.X), Mathf.Max(max.Y, t.Origin.Y), Mathf.Max(max.Z, t.Origin.Z));
        }
        return new BatchBounds((min + max) * 0.5f, (max - min).Length() * 0.5f);
    }

    private static MultiMesh BuildMultiMesh(Mesh mesh, List<Transform3D> transforms, Vector3 centre)
    {
        var multimesh = new MultiMesh
        {
            Mesh = mesh,
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            InstanceCount = transforms.Count,
        };
        // One native buffer upload (12 floats per Transform3D: three basis rows each followed by the
        // origin component) instead of a marshaled SetInstanceTransform call per instance.
        //
        // Origins are stored relative to `centre`, which the caller then gives the instance as its
        // transform: node * local is the identical world placement, but the instance finally has a
        // spatial origin. Godot measures a visibility range from that origin, so leaving every batch at
        // (0,0,0) — as this did — made every object switch level on the camera's distance from the map
        // origin rather than from the objects. FoliageBuilder.PackBuffer rebases for the same reason.
        var buffer = new float[transforms.Count * 12];
        for (int i = 0; i < transforms.Count; i++)
        {
            Transform3D t = transforms[i];
            Vector3 o3 = t.Origin - centre;
            int o = i * 12;
            buffer[o + 0] = t.Basis.X.X; buffer[o + 1] = t.Basis.Y.X; buffer[o + 2] = t.Basis.Z.X; buffer[o + 3] = o3.X;
            buffer[o + 4] = t.Basis.X.Y; buffer[o + 5] = t.Basis.Y.Y; buffer[o + 6] = t.Basis.Z.Y; buffer[o + 7] = o3.Y;
            buffer[o + 8] = t.Basis.X.Z; buffer[o + 9] = t.Basis.Y.Z; buffer[o + 10] = t.Basis.Z.Z; buffer[o + 11] = o3.Z;
        }
        multimesh.Buffer = buffer;

        return multimesh;
    }

    // Unturned's prefabs carry their own lower LOD levels; the port used to keep only LOD-0 and draw full
    // detail at every distance. UG_OBJECT_LOD=0 restores that for A/B measurement.
    // Public so a caller can skip loading the lower-level library altogether: with the flag off the
    // A/B control must not pay for meshes and materials that ObjectsBuilder would then ignore.
    public static bool ObjectLodEnabled => LodEnabled;

    private static readonly bool LodEnabled = EnvBool("UG_OBJECT_LOD", true);

    // Unity switches level by projected screen height, so the threshold scales with the object: a tree
    // holds its detail much further out than a crate. Approximate that with a multiple of the mesh's
    // bounding radius, tunable because the authored per-prefab thresholds are not extracted yet.
    //
    // Be aware of how little this does at the shipped cell size. AddLevels adds the batch's placement
    // radius on top, and that radius dominates the threshold whenever a cell is large next to the objects
    // inside it — sweeping this knob across its whole range then barely moves what is submitted. It only
    // starts to matter once cells are small enough for the two terms to be comparable, so
    // UG_OBJECT_LOD_CHUNK_METRES is the lever to reach for first.
    private static readonly float LodSwitchRadii = EnvFloat("UG_OBJECT_LOD_RADII", 6f, 2f, 200f);
    // Unity's LODGroup switches level outright unless cross-fade is explicitly authored, so a hard swap
    // is both the parity behaviour and the cheaper one: inside a fade margin Godot dithers the two levels
    // together, which draws BOTH. UG_OBJECT_LOD_FADE=1 opts back into the dithered swap.
    private static readonly bool LodFade = EnvBool("UG_OBJECT_LOD_FADE", false);

    // Cell size for groups that have a lower level, capped by ObjectChunkMetres above. A batch switches
    // level as a whole, so cells that are large next to their own switch distance make the choice
    // arbitrary and some poses get worse. Cutting further keeps shedding primitives but costs draw calls
    // at the poses that see the whole map at once, which is the same trade ObjectChunkMetres makes.
    private static readonly float LodChunkMetres = EnvFloat("UG_OBJECT_LOD_CHUNK_METRES", 1024f, 0f, 8192f);
    private static float LodFadeMargin => LodFade ? 8f : 0f;

    // The screen size a placement covers is its mesh radius times the scale it was placed at, and the
    // batch shares one visibility range, so the largest placement in the batch sets the distance: a
    // 2x-scaled copy must keep its detail twice as far out or it visibly swaps down while the unscaled
    // copies beside it do not.
    private static float SwitchDistanceFor(Mesh mesh, List<Transform3D> transforms)
    {
        float radius = mesh.GetAabb().Size.Length() * 0.5f;
        float maxScale = 0f;
        foreach (Transform3D transform in transforms)
        {
            Vector3 scale = transform.Basis.Scale.Abs();
            maxScale = Mathf.Max(maxScale, Mathf.Max(scale.X, Mathf.Max(scale.Y, scale.Z)));
        }
        return Mathf.Max(16f, radius * maxScale * LodSwitchRadii);
    }

    // One batch when the prefab has a single level, two sharing the same transforms when it has a lower
    // one: LOD-0 up to the switch distance, the lower level from there out. Godot's visibility range
    // keeps exactly one of the pair drawn, with a fade margin so the swap is not a pop.
    //
    // The range is measured from the batch's own centre to the camera, and the placements inside it are
    // spread up to `Radius` around that centre, so the threshold is pushed out by the radius. Without
    // that a cell whose centre is just past the switch distance would draw the coarse level for the
    // placement nearest the player as well. It also bounds what this can buy: a batch only ever switches
    // as a whole, so the win comes from cells that are small next to their own switch distance.
    private static int AddLevels(Node3D root, MultiMeshRidRenderer? renderer, ArrayMesh mesh,
        ArrayMesh? lodMesh, List<Transform3D> transforms)
    {
        BatchBounds bounds = BoundsOf(transforms);
        if (lodMesh == null)
        {
            AddRenderBatch(root, renderer, BuildMultiMesh(mesh, transforms, bounds.Centre), bounds.Centre);
            return 1;
        }
        float switchDistance = SwitchDistanceFor(mesh, transforms) + bounds.Radius;
        AddRenderBatch(root, renderer, BuildMultiMesh(mesh, transforms, bounds.Centre), bounds.Centre,
            visibilityEnd: switchDistance, visibilityMargin: LodFadeMargin);
        AddRenderBatch(root, renderer, BuildMultiMesh(lodMesh, transforms, bounds.Centre), bounds.Centre,
            visibilityBegin: switchDistance, visibilityMargin: LodFadeMargin);
        return 2;
    }

    private static void AddRenderBatch(Node3D root, MultiMeshRidRenderer? renderer, MultiMesh multimesh,
        Vector3 centre, float visibilityEnd = 0f, float visibilityMargin = 0f, float visibilityBegin = 0f)
    {
        if (renderer != null)
            renderer.Add(multimesh, new Transform3D(Basis.Identity, centre), shadows: true, visibilityEnd,
                visibilityMargin, visibilityBegin);
        else
            root.AddChild(new MultiMeshInstance3D
            {
                Multimesh = multimesh,
                Position = centre,
                VisibilityRangeBegin = visibilityBegin,
                VisibilityRangeEnd = visibilityEnd,
                VisibilityRangeBeginMargin = visibilityBegin > 0f ? visibilityMargin : 0f,
                VisibilityRangeEndMargin = visibilityEnd > 0f ? visibilityMargin : 0f,
                // Match the RID path: with no margin there is nothing to dither, and Self would still put
                // the batch on the transparent pass.
                VisibilityRangeFadeMode = visibilityMargin > 0f
                    ? GeometryInstance3D.VisibilityRangeFadeModeEnum.Self
                    : GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled,
            });
    }

    private static Node3D BuildFallbackBoxes(List<(Transform3D transform, Color color)> items, string label)
    {
        var root = new Node3D { Name = label + "Placeholders" };
        var mesh = new BoxMesh { Size = new Vector3(2, 2, 2) };
        var material = new StandardMaterial3D { VertexColorUseAsAlbedo = true };

        // Missing assets used to share one map-wide MultiMesh. Besides making its AABB span the whole
        // level, that kept every placeholder visible whenever any one of them was visible. Use the same
        // spatial cells as real objects while sharing the BoxMesh and material between all batches.
        if (ObjectChunkMetres > 0f && items.Count > 1)
        {
            var cells = new Dictionary<(int X, int Z), List<(Transform3D transform, Color color)>>();
            Vector3 anchor = items[0].transform.Origin;
            foreach ((Transform3D transform, Color _) in items)
                anchor = anchor.Min(transform.Origin);
            foreach ((Transform3D transform, Color color) in items)
            {
                (int X, int Z) cell = CellOf(transform.Origin, anchor, ObjectChunkMetres);
                if (!cells.TryGetValue(cell,
                    out List<(Transform3D transform, Color color)>? inCell))
                    cells[cell] = inCell = new List<(Transform3D, Color)>();
                inCell.Add((transform, color));
            }
            foreach (((int x, int z), List<(Transform3D transform, Color color)> inCell) in cells)
                root.AddChild(BuildFallbackBatch(inCell, mesh, material, $"Cell_{x}_{z}"));
        }
        else
        {
            root.AddChild(BuildFallbackBatch(items, mesh, material, "All"));
        }
        return root;
    }

    private static MultiMeshInstance3D BuildFallbackBatch(
        List<(Transform3D transform, Color color)> items, BoxMesh mesh,
        StandardMaterial3D material, string name)
    {
        var multimesh = new MultiMesh
        {
            Mesh = mesh,
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            InstanceCount = items.Count,
        };
        // One native buffer upload, same as BuildMultiMesh: with UseColors the stride is the 12 transform
        // floats followed by the instance's RGBA. The per-instance setters cost two marshaled calls each,
        // which is nothing on PEI (one fallback box) but tens of thousands on a cold-cache map.
        var buffer = new float[items.Count * 16];
        for (int i = 0; i < items.Count; i++)
        {
            Transform3D t = items[i].transform;
            Color c = items[i].color;
            int o = i * 16;
            buffer[o + 0] = t.Basis.X.X; buffer[o + 1] = t.Basis.Y.X; buffer[o + 2] = t.Basis.Z.X; buffer[o + 3] = t.Origin.X;
            buffer[o + 4] = t.Basis.X.Y; buffer[o + 5] = t.Basis.Y.Y; buffer[o + 6] = t.Basis.Z.Y; buffer[o + 7] = t.Origin.Y;
            buffer[o + 8] = t.Basis.X.Z; buffer[o + 9] = t.Basis.Y.Z; buffer[o + 10] = t.Basis.Z.Z; buffer[o + 11] = t.Origin.Z;
            buffer[o + 12] = c.R; buffer[o + 13] = c.G; buffer[o + 14] = c.B; buffer[o + 15] = c.A;
        }
        multimesh.Buffer = buffer;

        return new MultiMeshInstance3D
        {
            Multimesh = multimesh,
            Name = name,
            MaterialOverride = material,
        };
    }
}
