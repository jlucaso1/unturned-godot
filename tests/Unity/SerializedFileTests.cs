using System;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class SerializedFileTests
{
    [Fact]
    public void ParsesMinimalFile_WithTypeDependencies()
    {
        SerializedFile file = SerializedFile.Read(new SerializedFileBuilder { ClassId = 43 }.Build());

        SerializedObject obj = Assert.Single(file.Objects);
        Assert.Equal(43, obj.ClassId);
        Assert.Equal(100, obj.PathId);
        Assert.False(file.BigEndian);
        Assert.Single(obj.TypeTree); // the "Base" node
        Assert.Equal("Base", obj.TypeTree[0].Type);
    }

    [Fact]
    public void ReadsMonoBehaviourScriptId()
    {
        // classId 114 carries an extra 16-byte script id; parsing must still line up.
        SerializedFile file = SerializedFile.Read(new SerializedFileBuilder { ClassId = 114 }.Build());
        Assert.Equal(114, Assert.Single(file.Objects).ClassId);
    }

    [Fact]
    public void TypeTreeDisabled_LeavesEmptyTree()
    {
        SerializedFile file = SerializedFile.Read(new SerializedFileBuilder { EnableTypeTree = false }.Build());
        Assert.Empty(file.Objects[0].TypeTree);
    }

    [Fact]
    public void UnsupportedVersion_Throws()
    {
        // metadataSize, fileSize, version(=20, big-endian), dataOffset.
        byte[] bytes = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 20, 0, 0, 0, 0 };
        Assert.Throws<NotSupportedException>(() => SerializedFile.Read(bytes));
    }

    [Fact]
    public void ReaderFor_PositionsAtObjectData()
    {
        SerializedFile file = SerializedFile.Read(new SerializedFileBuilder().Build());
        UnityBinaryReader r = file.ReaderFor(file.Objects[0]);
        Assert.Equal(1, r.ReadByte()); // first byte of the object payload {1,2,3,4}
    }
}
