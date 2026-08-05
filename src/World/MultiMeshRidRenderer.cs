using System.Collections.Generic;
using Godot;
using UnturnedGodot.Assets;

namespace UnturnedGodot;

// One lifecycle node owns many RenderingServer instances. MultiMeshInstance3D is otherwise only a managed
// wrapper around this same RID state; large maps paid for ~18k wrappers with no per-node behaviour.
public partial class MultiMeshRidRenderer : Node3D
{
    private List<Entry> _entries = new();
    private readonly List<Rid> _instances = new();
    private readonly List<MultiMesh> _retainedMeshes = new();
    private readonly List<Transform3D> _localTransforms = new();
    // What each instance draws, in Unturned's own asset classification, kept only when somebody actually
    // supplied one — the foliage renderer and the placeholder boxes do not, and an array of Unknown per
    // instance would be retained memory saying nothing. Aligned with _instances by index.
    private EObjectType[]? _instanceCategories;
    private readonly HashSet<EObjectType> _hiddenCategories = new();
    // Whether every batch asked for the same shadow setting. The dev console's per-renderer shadow switch
    // restores that setting when it is turned back on, and it can only do that honestly for a renderer
    // whose batches agreed in the first place.
    private bool _uniformShadows = true;
    private bool _shadowsAsBuilt = true;
    private bool _shadowsSuppressed;
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
        float VisibilityEnd, float VisibilityMargin, float VisibilityBegin, EObjectType Category);

    // visibilityBegin hides the batch until the camera is that far away, which is what lets an authored
    // lower LOD take over from the full-detail one at a distance instead of both drawing at once.
    //
    // `category` is only ever read back by the dev console, which needs to be able to stop submitting one
    // kind of object without touching the rest. Leaving it Unknown costs nothing and means "not sorted".
    public void Add(MultiMesh mesh, Transform3D transform, bool shadows = true,
        float visibilityEnd = 0f, float visibilityMargin = 0f, float visibilityBegin = 0f,
        EObjectType category = EObjectType.Unknown)
    {
        if (_entries.Count == 0)
            _shadowsAsBuilt = shadows;
        else if (shadows != _shadowsAsBuilt)
            _uniformShadows = false;
        _entries.Add(new Entry(mesh, transform, shadows, visibilityEnd, visibilityMargin, visibilityBegin,
            category));
    }

    public override void _Ready()
    {
        SetNotifyTransform(true);
        Rid scenario = GetWorld3D().Scenario;
        bool compact = !EnvFlag.IsOn(OS.GetEnvironment("UG_KEEP_RID_UPLOAD_METADATA"), whenUnset: false);
        if (compact)
        {
            _retainedMeshes.Capacity = _entries.Count;
            _localTransforms.Capacity = _entries.Count;
        }
        _instanceCategories = CategoriesOf(_entries);
        foreach (Entry entry in _entries)
        {
            Rid instance = RenderingServer.InstanceCreate();
            RenderingServer.InstanceSetBase(instance, entry.Mesh.GetRid());
            RenderingServer.InstanceSetTransform(instance, GlobalTransform * entry.Transform);
            RenderingServer.InstanceGeometrySetCastShadowsSetting(instance, entry.Shadows
                ? RenderingServer.ShadowCastingSetting.On : RenderingServer.ShadowCastingSetting.Off);
            if (entry.VisibilityEnd > 0f || entry.VisibilityBegin > 0f)
                RenderingServer.InstanceGeometrySetVisibilityRange(instance, entry.VisibilityBegin,
                    entry.VisibilityEnd, entry.VisibilityBegin > 0f ? entry.VisibilityMargin : 0f,
                    entry.VisibilityMargin,
                    // Self fades by dithering, which draws both sides of a swap for the width of the
                    // margin. With no margin there is nothing to dither, so ask for the hard switch.
                    entry.VisibilityMargin > 0f
                        ? RenderingServer.VisibilityRangeFadeMode.Self
                        : RenderingServer.VisibilityRangeFadeMode.Disabled);
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
        UpdateVisibility();
    }

    // Null when nothing was sorted, which is the common case: retaining one entry per instance to say
    // "Unknown" over and over is the kind of per-instance metadata this node exists to avoid.
    private static EObjectType[]? CategoriesOf(List<Entry> entries)
    {
        bool sorted = false;
        foreach (Entry entry in entries)
        {
            if (entry.Category == EObjectType.Unknown)
                continue;
            sorted = true;
            break;
        }
        if (!sorted)
            return null;

        var categories = new EObjectType[entries.Count];
        for (int i = 0; i < entries.Count; i++)
            categories[i] = entries[i].Category;
        return categories;
    }

    // How many instances this renderer submits for one kind of object. Zero means the switch below has
    // nothing to act on, which the console reports rather than pretending the toggle did something.
    public int CountOf(EObjectType category)
    {
        if (_instanceCategories == null)
            return 0;
        int count = 0;
        foreach (EObjectType candidate in _instanceCategories)
            if (candidate == category)
                count++;
        return count;
    }

    // Stops submitting one kind of object while leaving the rest of the batch alone. Rendering only:
    // the collision bodies, ladder volumes and the server's damage ledger are separate nodes and are
    // deliberately untouched, so switching the trees off does not let the player walk through one.
    public int SetCategoryVisible(EObjectType category, bool visible)
    {
        bool changed = visible
            ? _hiddenCategories.Remove(category)
            : _hiddenCategories.Add(category);
        if (changed)
            UpdateVisibility();
        return CountOf(category);
    }

    // Takes every batch here out of the shadow pass, or puts it back the way it was built. False means
    // this renderer's batches did not agree on a shadow setting, so there is no "the way it was built"
    // to restore and the request is refused rather than guessed at.
    public bool SetShadowsEnabled(bool enabled)
    {
        if (!_uniformShadows)
            return false;
        _shadowsSuppressed = !enabled;
        RenderingServer.ShadowCastingSetting setting = enabled && _shadowsAsBuilt
            ? RenderingServer.ShadowCastingSetting.On
            : RenderingServer.ShadowCastingSetting.Off;
        foreach (Rid instance in _instances)
            RenderingServer.InstanceGeometrySetCastShadowsSetting(instance, setting);
        return true;
    }

    public bool ShadowsSuppressed => _shadowsSuppressed;

    public override void _Notification(int what)
    {
        if (what == NotificationVisibilityChanged)
        {
            UpdateVisibility();
            return;
        }
        if (what != NotificationTransformChanged)
            return;
        if (_localTransforms.Count == _instances.Count)
            for (int i = 0; i < _instances.Count; i++)
                RenderingServer.InstanceSetTransform(_instances[i], GlobalTransform * _localTransforms[i]);
        else if (_entries.Count == _instances.Count)
            for (int i = 0; i < _instances.Count; i++)
                RenderingServer.InstanceSetTransform(_instances[i], GlobalTransform * _entries[i].Transform);
    }

    private void UpdateVisibility()
    {
        bool visible = IsVisibleInTree();
        if (!visible || _hiddenCategories.Count == 0 || _instanceCategories == null)
        {
            foreach (Rid instance in _instances)
                RenderingServer.InstanceSetVisible(instance, visible);
            return;
        }
        for (int i = 0; i < _instances.Count; i++)
            RenderingServer.InstanceSetVisible(_instances[i],
                !_hiddenCategories.Contains(_instanceCategories[i]));
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
