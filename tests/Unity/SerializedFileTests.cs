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
    public void ParsesVersion15File()
    {
        // The Unity 5.x per-map bundle format: no second header block, and objects reference their type
        // by class id (u16) rather than a type index.
        SerializedFile file = SerializedFile.Read(new SerializedFileBuilder { Version = 15, ClassId = 28 }.Build());

        SerializedObject obj = Assert.Single(file.Objects);
        Assert.Equal(28, obj.ClassId); // Texture2D
        Assert.Equal(100, obj.PathId);
        Assert.Equal("Base", Assert.Single(obj.TypeTree).Type);
        Assert.Equal(1, file.ReaderFor(obj).ReadByte()); // payload {1,2,3,4} lands at the data offset
    }

    [Fact]
    public void Version15_NegativeTypeClassId_ReadsWithoutTypeTree()
    {
        // A negative pre-16 type class id (a MonoBehaviour/script type) carries an extra script hash, and
        // its object then matches no built-in type tree; the parse must stay aligned and not crash.
        SerializedFile file = SerializedFile.Read(new SerializedFileBuilder { Version = 15, ClassId = -1 }.Build());
        Assert.Empty(Assert.Single(file.Objects).TypeTree);
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
