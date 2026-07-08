using System.Collections.Generic;
using System.Text;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class UnityRawBundleTests
{
    [Fact]
    public void IsRaw_DetectsSignature()
    {
        Assert.True(UnityRawBundle.IsRaw(Encoding.ASCII.GetBytes("UnityRaw\0junk")));
        Assert.False(UnityRawBundle.IsRaw(Encoding.ASCII.GetBytes("UnityFS\0")));
        Assert.False(UnityRawBundle.IsRaw(new byte[3])); // too short
    }

    [Fact]
    public void Read_WrongSignature_Throws() =>
        Assert.Throws<System.NotSupportedException>(
            () => UnityRawBundle.Read(Encoding.ASCII.GetBytes("UnityFS\0")));

    [Fact]
    public void Read_ExtractsEntryPayload()
    {
        byte[] payload = { 10, 20, 30, 40, 50 };
        byte[] bundle = BuildRawBundle("CAB-test", payload);

        UnityRawBundle raw = UnityRawBundle.Read(bundle);

        KeyValuePair<string, byte[]> file = Assert.Single(raw.Files);
        Assert.Equal("CAB-test", file.Key);
        Assert.Equal(payload, file.Value);
    }

    // Writes a minimal UnityRaw bundle (big-endian header) wrapping a single uncompressed entry.
    private static byte[] BuildRawBundle(string entryName, byte[] payload)
    {
        var pre = new List<byte>();
        Cstr(pre, "UnityRaw");
        U32(pre, 3);              // stream version
        Cstr(pre, "5.x.x");
        Cstr(pre, "5.2.4f1");
        U32(pre, 0);              // minimum streamed bytes
        int headerSizePos = pre.Count;
        U32(pre, 0);              // header size (patched below)
        U32(pre, 0);              // bytes to download
        U32(pre, 1);              // level count
        U32(pre, (uint)payload.Length); // compressed
        U32(pre, (uint)payload.Length); // uncompressed

        int headerSize = pre.Count;
        Patch(pre, headerSizePos, headerSize);

        var table = new List<byte>();
        U32(table, 1);            // entry count
        Cstr(table, entryName);
        int offsetPos = table.Count;
        U32(table, 0);            // offset relative to headerSize (patched below)
        U32(table, (uint)payload.Length);
        Patch(table, offsetPos, table.Count); // payload follows the entry table

        var all = new List<byte>();
        all.AddRange(pre);
        all.AddRange(table);
        all.AddRange(payload);
        return all.ToArray();
    }

    private static void Cstr(List<byte> b, string s) { b.AddRange(Encoding.ASCII.GetBytes(s)); b.Add(0); }
    private static void U32(List<byte> b, uint v) { for (int i = 3; i >= 0; i--) b.Add((byte)(v >> (i * 8))); }
    private static void Patch(List<byte> b, int pos, int v)
    {
        for (int i = 0; i < 4; i++) b[pos + i] = (byte)(v >> ((3 - i) * 8));
    }
}
