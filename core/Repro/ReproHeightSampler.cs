using Godot;

namespace UnturnedGodot.Repro;

// The terrain heightfield a dump carries: a small regular grid around the incident, bilinearly
// sampled. It is what the simulation falls back to when nothing else answers for the ground — the same
// role HeightmapSampler plays in a live session, over the patch that fits in a bug report instead of
// over the whole map.
public sealed class ReproHeightSampler
{
    private readonly ReproHeightPatch _patch;
    private readonly Vector3 _origin;

    private ReproHeightSampler(ReproHeightPatch patch)
    {
        _patch = patch;
        _origin = ReproVector.To(patch.Origin);
    }

    public static ReproHeightSampler? From(ReproHeightPatch? patch) =>
        patch is { Width: > 1, Depth: > 1, Cell: > 0f }
            && patch.Heights.Length >= patch.Width * patch.Depth
            ? new ReproHeightSampler(patch)
            : null;

    public bool Sample(float x, float z, out float y)
    {
        y = 0f;
        float gx = (x - _origin.X) / _patch.Cell;
        float gz = (z - _origin.Z) / _patch.Cell;
        var x0 = (int)Mathf.Floor(gx);
        var z0 = (int)Mathf.Floor(gz);
        if (x0 < 0 || z0 < 0 || x0 + 1 >= _patch.Width || z0 + 1 >= _patch.Depth)
            return false;
        float h00 = _patch.Heights[(z0 * _patch.Width) + x0];
        float h10 = _patch.Heights[(z0 * _patch.Width) + x0 + 1];
        float h01 = _patch.Heights[((z0 + 1) * _patch.Width) + x0];
        float h11 = _patch.Heights[((z0 + 1) * _patch.Width) + x0 + 1];
        // NaN marks a cell the sampler had no answer for; interpolating across one would invent ground.
        if (float.IsNaN(h00) || float.IsNaN(h10) || float.IsNaN(h01) || float.IsNaN(h11))
            return false;
        float fx = gx - x0, fz = gz - z0;
        y = Mathf.Lerp(Mathf.Lerp(h00, h10, fx), Mathf.Lerp(h01, h11, fx), fz);
        return true;
    }
}
