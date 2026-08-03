using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests;

// E2E validation of the Unity parser against the real core bundle. Reports a SKIP when it is absent, and
// the real-data job turns that skip into a failure. Ground-truth counts come from UnityPy reading the same
// file.
[Trait("Category", "RealData")]
public class UnityBundleRealTests
{
    private static string BundlePath() => GameData.MasterBundle!;

    private static byte[]? SerializedFileBytes(UnityBundle bundle)
    {
        foreach (KeyValuePair<string, byte[]> f in bundle.Files)
            if (!f.Key.EndsWith(".resS") && !f.Key.EndsWith(".resource"))
                return f.Value;
        return null;
    }

    [RealDataFact(RequiresMasterBundle = true)]
    public void ParsesRealBundle_ObjectCountsMatchUnityPy()
    {
        string path = BundlePath();

        // Decode only the SerializedFile prefix (~179 MB) instead of the whole 1.4 GB block.
        UnityBundle bundle = UnityBundle.Read(File.ReadAllBytes(path), maxDecompressedBytes: 200_000_000);

        byte[]? serialized = SerializedFileBytes(bundle);
        Assert.NotNull(serialized);

        SerializedFile file = SerializedFile.Read(serialized!);

        var counts = new Dictionary<int, int>();
        foreach (SerializedObject o in file.Objects)
            counts[o.ClassId] = counts.GetValueOrDefault(o.ClassId) + 1;

        // Asserted as ONE tuple rather than three statements, so a mismatch reports every class at
        // once. Separately, the first failing Assert.Equal ended the test, and a content update that
        // moved more than one count could only be discovered one CI round at a time — which is exactly
        // how this was found: GameObject went 24341 -> 24342 while Mesh and MeshFilter never got read.
        //
        // A moving count here is normally the game, not the parser: these are counts OF the shipped
        // bundle, and the fetch job re-downloads precisely when Valve's depot manifest changes. The
        // signature to check before touching a number is whether the others held — a parser regression
        // has no reason to miscount one class and get the neighbouring two exactly right.
        var actual = (Mesh: counts.GetValueOrDefault(43),
            GameObject: counts.GetValueOrDefault(1),
            MeshFilter: counts.GetValueOrDefault(33));
        Assert.Equal((Mesh: 4560, GameObject: 24342, MeshFilter: 9381), actual);
    }

    [RealDataFact(RequiresMasterBundle = true)]
    public void ReadsRealMesh_EndToEnd_ThroughGenericReader()
    {
        string path = BundlePath();

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
