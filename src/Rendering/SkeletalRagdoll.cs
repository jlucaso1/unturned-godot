using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Player;

namespace UnturnedGodot;

// A corpse that falls a limb at a time — RagdollTool's Ragdoll_* prefab, rebuilt against the rig the body
// was already wearing.
//
// One rigid body per bone that the prefab hangs one off, tied by cone-twist joints with the prefab's own
// limits, and every frame the bones are written back from the bodies so the skinned mesh follows.
//
// NOT built from PhysicalBone3D, which is what Godot's own ragdoll documentation reaches for. That ties
// each bone to the nearest ANCESTOR that carries a body, and this rig has three roots — Spine, Left_Hip
// and Right_Hip are all parentless, because the character's pelvis is the Skeleton node rather than a
// bone. Under PhysicalBone3D the legs would have no ancestor with a body and would simply fall away from
// the torso. Explicit bodies and joints can express the prefab's own chain, where the legs hang off the
// pelvis, and that chain is the whole shape of the thing.
public sealed partial class SkeletalRagdoll : Node3D
{
    // Godot's ConeTwistJoint3D takes radians; the prefab's limits are degrees.
    private const float MinSpanDegrees = 1f;

    // The pelvis has no bone of its own: the rig's roots are Spine, Left_Hip and Right_Hip, and the
    // pelvis they all hang from is the Skeleton node. Its body is built where the spine is, and the
    // spine is jointed to it — which is the prefab's own chain, with the pelvis standing where the
    // skeleton root does.
    private sealed class Limb
    {
        public required RigidBody3D Body;
        public required int Bone;              // -1 for the pelvis, which drives no bone
        public required Transform3D BoneToBody; // the bone's pose in the body's frame, fixed at birth
    }

    private readonly List<Limb> _limbs = new();
    private Skeleton3D? _skeleton;

    // Whether the bodies are being stepped. False until Begin, and false again once the corpse is freed.
    public bool Simulating { get; private set; }

    // How many bodies the ragdoll ended up with. Read by the tests, and by anything diagnosing a rig the
    // definition did not fit.
    public int LimbCount => _limbs.Count;

    // Builds the bodies for `skeleton` from `definition` and starts them falling.
    //
    // Returns false when the rig and the definition do not meet — a rig with none of the named bones, or
    // a definition with nothing in it. The caller then tumbles the body as one piece instead, which is
    // what a session with no readable install gets.
    public bool Begin(Skeleton3D skeleton, RagdollDefinition definition, Vector3 impulse)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(definition);
        if (Simulating || !definition.IsUsable)
            return false;

        _skeleton = skeleton;
        var byName = new Dictionary<string, Limb>(StringComparer.Ordinal);

        foreach (RagdollBone bone in definition.InParentOrder())
        {
            // The pelvis has no bone of its own: it is built where the spine is, and the spine hangs off
            // it. Everything else has to name a bone the rig actually carries.
            bool isPelvis = string.Equals(bone.Name, RagdollDefinition.PelvisName, StringComparison.Ordinal);
            int index = skeleton.FindBone(isPelvis ? RagdollDefinition.ImpulseBone : bone.Name);
            if (index < 0)
                continue;

            Transform3D pose = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(index);
            var body = new RigidBody3D
            {
                Mass = MathF.Max(0.01f, bone.Mass),
                // The prefab's own damping. Without it a corpse skids and spins for its whole lifetime.
                LinearDamp = 0.01f,
                AngularDamp = 0.05f,
                LinearDampMode = RigidBody3D.DampMode.Replace,
                AngularDampMode = RigidBody3D.DampMode.Replace,
                // Corpses collide with the world and with nothing else — not with each other, and not
                // with the player. The original's debris is on its own layer for the same reason.
                CollisionLayer = 0,
                CollisionMask = CollisionLayers.World,
            };
            AddChild(body);
            body.GlobalTransform = new Transform3D(pose.Basis.Orthonormalized(), pose.Origin);
            body.AddChild(ShapeFor(bone, pose.Basis.Scale));

            var limb = new Limb
            {
                Body = body,
                Bone = isPelvis ? -1 : index,
                // Fixed once: the body is created ON the bone, so the bone's pose in the body's frame is
                // whatever the orthonormalisation and the scale left behind. Recomputing it per frame
                // from the live pose would feed the body's own drift back into the bone.
                BoneToBody = new Transform3D(pose.Basis.Orthonormalized(), pose.Origin).AffineInverse()
                    * pose,
            };
            _limbs.Add(limb);
            byName[bone.Name] = limb;

            if (!bone.IsRoot && byName.TryGetValue(bone.Parent, out Limb? parent))
                AddChild(JointFor(bone, body, parent.Body));
        }

        if (_limbs.Count == 0)
            return false;

        // RagdollTool pushes the SPINE, not the pelvis and not the centre of mass: a shove applied
        // anywhere else spins the corpse about its own limbs instead of throwing it.
        if (impulse != Vector3.Zero
            && (byName.GetValueOrDefault(RagdollDefinition.ImpulseBone)
                ?? byName.GetValueOrDefault(RagdollDefinition.PelvisName)) is { } pushed)
        {
            pushed.Body.ApplyImpulse(impulse * Ragdolls.ImpulseScale);
        }

        Simulating = true;
        return true;
    }

    // The bone poses follow the bodies from here on. Written in _PhysicsProcess rather than _Process
    // because that is when the bodies moved; writing them on the render frame samples the same physics
    // step twice on a machine drawing faster than it steps.
    public override void _PhysicsProcess(double delta)
    {
        if (!Simulating || _skeleton == null)
            return;

        Transform3D toSkeleton = _skeleton.GlobalTransform.AffineInverse();
        foreach (Limb limb in _limbs)
        {
            if (limb.Bone < 0)
                continue;
            _skeleton.SetBoneGlobalPose(limb.Bone,
                toSkeleton * limb.Body.GlobalTransform * limb.BoneToBody);
        }
    }

    // The prefab's collider, as a Godot shape. Unity authors these in the bone's own space, so the box
    // carries the bone's scale with it — a mega's limbs are half again as large, and a box built at unit
    // scale would leave one rattling around inside a body it does not fill.
    private static CollisionShape3D ShapeFor(RagdollBone bone, Vector3 scale)
    {
        Vector3 size = bone.ColliderSize * scale.Abs();
        Vector3 centre = bone.ColliderCenter * scale;
        return new CollisionShape3D
        {
            Shape = new BoxShape3D
            {
                // A zero extent is a shape the physics server refuses; the prefab has none, but a
                // partially-read field would produce one.
                Size = new Vector3(MathF.Max(0.01f, size.X), MathF.Max(0.01f, size.Y),
                    MathF.Max(0.01f, size.Z)),
            },
            // The Z-mirror every other Unity-space value in this port goes through: the collider centre
            // is authored in the bone's Unity frame.
            Position = new Vector3(centre.X, centre.Y, -centre.Z),
        };
    }

    // A CharacterJoint as a cone-twist. The anchor is (0,0,0) on every joint the prefab ships, so both
    // ends are pinned at the child body's own origin — which is the bone's joint, where the limb pivots.
    private static ConeTwistJoint3D JointFor(RagdollBone bone, RigidBody3D child, RigidBody3D parent)
    {
        var joint = new ConeTwistJoint3D
        {
            GlobalTransform = child.GlobalTransform,
            NodeA = parent.GetPath(),
            NodeB = child.GetPath(),
            // Degrees off the prefab, radians into the joint. Clamped away from zero: a span of exactly
            // zero is a welded limb, and a partially-read joint would produce one.
            SwingSpan = Mathf.DegToRad(MathF.Max(MinSpanDegrees, bone.SwingSpanDegrees)),
            TwistSpan = Mathf.DegToRad(MathF.Max(MinSpanDegrees, bone.TwistSpanDegrees)),
        };

        // Softness and Relaxation are deliberately NOT set. Unity's SoftJointLimit carries a spring and a
        // bounciness beside each limit, and the obvious translation is those two parameters — but this
        // project runs Jolt, which implements neither and warns once per joint per corpse that it is
        // ignoring them. Setting a parameter the engine discards buys nothing and fills the log during
        // exactly the moment a player is looking at the result.
        return joint;
    }

    // Stops driving the skeleton and drops the bodies. The corpse's mesh keeps whatever pose it was left
    // in, which is what the caller then frees along with this node.
    public void Stop()
    {
        Simulating = false;
        _skeleton = null;
    }
}
