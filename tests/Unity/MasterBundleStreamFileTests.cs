using System;
using System.IO;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

// OpenFile is the path every real load takes: the bundle is read off disk as the decoder consumes it,
// instead of being held in memory whole. It has to agree with Open(byte[]) on every shape.
public class MasterBundleStreamFileTests
{
    private static byte[] LzmaBundle(byte[] sf, byte[] resS, bool atEnd = false) =>
        new UnityFsBuilder { LzmaBlocks = true, BlockInfoAtEnd = atEnd }
            .Add("CAB-x", sf)
            .Add("CAB-x.resS", resS)
            .Build();

    private static string WriteBundle(TempDir dir, string name, byte[] bundle)
    {
        dir.Write(name, bundle);
        return Path.Combine(dir.Path, name);
    }

    private static byte[] Read(MasterBundleStream stream, int count) => stream.Read(count);

    [Fact]
    public void OpenFile_ReadsTheSameNodesAndBytesAsTheInMemoryReader()
    {
        var serialized = new byte[3000];
        for (int i = 0; i < serialized.Length; i++)
            serialized[i] = (byte)i;
        var resS = new byte[500];
        for (int i = 0; i < resS.Length; i++)
            resS[i] = (byte)(255 - i);

        byte[] bundle = LzmaBundle(serialized, resS);
        using var dir = new TempDir();
        string path = WriteBundle(dir, "core_linux.masterbundle", bundle);

        using MasterBundleStream? fromFile = MasterBundleStream.OpenFile(path);
        using MasterBundleStream? fromMemory = MasterBundleStream.Open(bundle);

        Assert.NotNull(fromFile);
        Assert.NotNull(fromMemory);
        Assert.Equal(fromMemory!.TotalSize, fromFile!.TotalSize);
        Assert.Equal(fromMemory.Nodes.Count, fromFile.Nodes.Count);
        for (int i = 0; i < fromMemory.Nodes.Count; i++)
        {
            Assert.Equal(fromMemory.Nodes[i].Path, fromFile.Nodes[i].Path);
            Assert.Equal(fromMemory.Nodes[i].Offset, fromFile.Nodes[i].Offset);
            Assert.Equal(fromMemory.Nodes[i].Size, fromFile.Nodes[i].Size);
        }

        Assert.Equal(serialized, Read(fromFile, serialized.Length));
        Assert.Equal(resS, Read(fromFile, resS.Length));
    }

    [Fact]
    public void OpenFile_BlockTableStoredAtTheEnd_IsStillFound()
    {
        // The one part of the layout the front of the file does not contain.
        byte[] bundle = LzmaBundle(new byte[] { 4, 5, 6 }, new byte[] { 9 }, atEnd: true);
        using var dir = new TempDir();

        using MasterBundleStream? stream =
            MasterBundleStream.OpenFile(WriteBundle(dir, "atend.masterbundle", bundle));

        Assert.NotNull(stream);
        Assert.Equal(new byte[] { 4, 5, 6 }, Read(stream!, 3));
    }

    [Fact]
    public void OpenFile_ShapesItCannotStream_ReturnAndLeaveNoOpenHandle()
    {
        using var dir = new TempDir();

        // Not a bundle at all, and a file that is not there.
        Assert.Null(MasterBundleStream.OpenFile(WriteBundle(dir, "junk.masterbundle", new byte[] { 1, 2, 3 })));
        Assert.Null(MasterBundleStream.OpenFile(Path.Combine(dir.Path, "absent.masterbundle")));

        // A directory in place of the file.
        Directory.CreateDirectory(Path.Combine(dir.Path, "a.masterbundle"));
        Assert.Null(MasterBundleStream.OpenFile(Path.Combine(dir.Path, "a.masterbundle")));

        // The handle must not survive a rejected open: the fallback decoder opens the same file straight
        // after, and a leak here is one descriptor per attempted bundle. File locking is advisory on
        // Linux, so re-opening proves nothing — count the descriptors pointing at THIS file. Counting all
        // of them instead made the test flaky, since the suite runs classes in parallel and any other
        // test opening a file moved the total.
        string path = WriteBundle(dir, "junk2.masterbundle", new byte[] { 1, 2, 3 });
        if (!Directory.Exists("/proc/self/fd"))
            return;

        for (int attempt = 0; attempt < 32; attempt++)
            Assert.Null(MasterBundleStream.OpenFile(path));

        Assert.Equal(0, OpenDescriptorsFor(path));
    }

    // How many of this process's descriptors point at `path`, read through /proc (Linux only).
    private static int OpenDescriptorsFor(string path)
    {
        int open = 0;
        foreach (string entry in Directory.GetFileSystemEntries("/proc/self/fd"))
        {
            try
            {
                if (File.ResolveLinkTarget(entry, returnFinalTarget: true)?.FullName == path)
                    open++;
            }
            catch (IOException)
            {
                // the descriptor closed while we were looking at it
            }
        }

        return open;
    }

    [Fact]
    public void OpenFile_MultiBlockBundle_IsRejectedLikeTheInMemoryReader()
    {
        byte[] bundle = new UnityFsBuilder { BlockCount = 2 }.Add("CAB-x", new byte[16]).Build();
        using var dir = new TempDir();

        Assert.Null(MasterBundleStream.Open(bundle));
        Assert.Null(MasterBundleStream.OpenFile(WriteBundle(dir, "two.masterbundle", bundle)));
    }
}
