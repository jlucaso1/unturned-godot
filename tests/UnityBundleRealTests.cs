using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests;

// E2E validation of the Unity parser against the real core bundle. Self-skips when absent.
// Ground-truth counts come from UnityPy reading the same file.
public class UnityBundleRealTests
{
    private static string? BundlePath() => GameData.MasterBundle;

    private static byte[]? SerializedFileBytes(UnityBundle bundle)
    {
        foreach (KeyValuePair<string, byte[]> f in bundle.Files)
            if (!f.Key.EndsWith(".resS") && !f.Key.EndsWith(".resource"))
                return f.Value;
        return null;
    }

    [Fact]
    public void ParsesRealBundle_ObjectCountsMatchUnityPy()
    {
        string? path = BundlePath();
        if (path == null) return;

        // Decode only the SerializedFile prefix (~179 MB) instead of the whole 1.4 GB block.
        UnityBundle bundle = UnityBundle.Read(File.ReadAllBytes(path), maxDecompressedBytes: 200_000_000);

        byte[]? serialized = SerializedFileBytes(bundle);
        Assert.NotNull(serialized);

        SerializedFile file = SerializedFile.Read(serialized!);

        var counts = new Dictionary<int, int>();
        foreach (SerializedObject o in file.Objects)
            counts[o.ClassId] = counts.GetValueOrDefault(o.ClassId) + 1;

        Assert.Equal(4560, counts[43]);   // Mesh
        Assert.Equal(24341, counts[1]);   // GameObject
        Assert.Equal(9381, counts[33]);   // MeshFilter
    }

    [Fact]
    public void ReadsRealMesh_EndToEnd_ThroughGenericReader()
    {
        string? path = BundlePath();
        if (path == null) return;

        UnityBundle bundle = UnityBundle.Read(File.ReadAllBytes(path), maxDecompressedBytes: 200_000_000);
        SerializedFile file = SerializedFile.Read(SerializedFileBytes(bundle)!);

        int usableCount = 0;
        foreach (SerializedObject obj in file.Objects)
        {
            if (obj.ClassId != 43) // Mesh
                continue;

            Dictionary<string, object> dict = TypeTreeReader.Read(obj.TypeTree, file.ReaderFor(obj));
            UnityMesh mesh = UnityMesh.Read(dict);
            if (!mesh.Usable)
                continue;

            // Geometry must be self-consistent: every index in range, triangle-divisible.
            Assert.Equal(0, mesh.Indices.Length % 3);
            foreach (int i in mesh.Indices)
                Assert.InRange(i, 0, mesh.Vertices.Length - 1);

            if (++usableCount >= 200)
                break;
        }

        Assert.True(usableCount >= 100, $"only {usableCount} meshes decoded cleanly");
    }
}
