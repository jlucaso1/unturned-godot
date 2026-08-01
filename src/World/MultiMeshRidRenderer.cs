using System.Collections.Generic;
using Godot;

namespace UnturnedGodot;

// One lifecycle node owns many RenderingServer instances. MultiMeshInstance3D is otherwise only a managed
// wrapper around this same RID state; large maps paid for ~18k wrappers with no per-node behaviour.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class MultiMeshRidRenderer : Node3D
{
    private List<Entry> _entries = new();
    private readonly List<Rid> _instances = new();
    private readonly List<MultiMesh> _retainedMeshes = new();
    private readonly List<Transform3D> _localTransforms = new();
    public int InstanceCount => _instances.Count > 0 ? _instances.Count : _entries.Count;
    public IEnumerable<MultiMesh> MultiMeshes
    {
        get
        {
            if (_retainedMeshes.Count > 0)
                foreach (MultiMesh mesh in _retainedMeshes) yield return mesh;
            else
                foreach (Entry entry in _entries) yield return entry.Mesh;
        }
    }

    private readonly record struct Entry(MultiMesh Mesh, Transform3D Transform, bool Shadows,
        float VisibilityEnd, float VisibilityMargin);

    public void Add(MultiMesh mesh, Transform3D transform, bool shadows = true,
        float visibilityEnd = 0f, float visibilityMargin = 0f) =>
        _entries.Add(new Entry(mesh, transform, shadows, visibilityEnd, visibilityMargin));

    public override void _Ready()
    {
        SetNotifyTransform(true);
        Rid scenario = GetWorld3D().Scenario;
        bool compact = OS.GetEnvironment("UG_KEEP_RID_UPLOAD_METADATA") != "1";
        if (compact)
        {
            _retainedMeshes.Capacity = _entries.Count;
            _localTransforms.Capacity = _entries.Count;
        }
        foreach (Entry entry in _entries)
        {
            Rid instance = RenderingServer.InstanceCreate();
            RenderingServer.InstanceSetBase(instance, entry.Mesh.GetRid());
            RenderingServer.InstanceSetTransform(instance, GlobalTransform * entry.Transform);
            RenderingServer.InstanceGeometrySetCastShadowsSetting(instance, entry.Shadows
                ? RenderingServer.ShadowCastingSetting.On : RenderingServer.ShadowCastingSetting.Off);
            if (entry.VisibilityEnd > 0f)
                RenderingServer.InstanceGeometrySetVisibilityRange(instance, 0f, entry.VisibilityEnd,
                    0f, entry.VisibilityMargin,
                    RenderingServer.VisibilityRangeFadeMode.Self);
            RenderingServer.InstanceSetScenario(instance, scenario);
            _instances.Add(instance);
            if (compact)
            {
                _retainedMeshes.Add(entry.Mesh);
                _localTransforms.Add(entry.Transform);
            }
        }
        if (compact)
            _entries = new List<Entry>();
    }

    public override void _Notification(int what)
    {
        if (what != NotificationTransformChanged)
            return;
        if (_localTransforms.Count == _instances.Count)
            for (int i = 0; i < _instances.Count; i++)
                RenderingServer.InstanceSetTransform(_instances[i], GlobalTransform * _localTransforms[i]);
        else if (_entries.Count == _instances.Count)
            for (int i = 0; i < _instances.Count; i++)
                RenderingServer.InstanceSetTransform(_instances[i], GlobalTransform * _entries[i].Transform);
    }

    public override void _ExitTree()
    {
        foreach (Rid instance in _instances)
            if (instance.IsValid) RenderingServer.FreeRid(instance);
        _instances.Clear();
        _retainedMeshes.Clear();
        _localTransforms.Clear();
        _entries.Clear(); // releases MultiMesh wrappers after their instances are gone
    }
}
