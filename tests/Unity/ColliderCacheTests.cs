using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class ColliderCacheTests
{
    private static readonly Transform3D Pose = new(
        new Basis(new Vector3(1, 0, 0), new Vector3(0, 2, 0), new Vector3(0, 0, 3)),
        new Vector3(4, 5, 6));

    [Fact]
    public void RoundTrips_EveryColliderKind()
    {
        var colliders = new List<CachedCollider>
        {
            CachedCollider.Box(Pose, new Vector3(0.1f, 0.2f, 0.3f), new Vector3(2, 3, 4)),
            CachedCollider.Sphere(Pose, new Vector3(1, 0, 0), 1.5f),
            CachedCollider.Capsule(Pose, new Vector3(0, 8, 0), 0.5f, 18f, 1),
            CachedCollider.Mesh(Pose,
                new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) },
                new[] { 0, 1, 2 }),
        };

        using var ms = new MemoryStream();
        ColliderCache.Write(ms, colliders);
        ms.Position = 0;
        List<CachedCollider> read = ColliderCache.Read(ms);

        Assert.Equal(4, read.Count);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(colliders[i].Kind, read[i].Kind);
            Assert.Equal(Pose, read[i].LocalToRoot);
        }

        Assert.Equal(new Vector3(0.1f, 0.2f, 0.3f), read[0].Center);
        Assert.Equal(new Vector3(2, 3, 4), read[0].Size);
        Assert.Equal(1.5f, read[1].Radius);
        Assert.Equal(0.5f, read[2].Radius);
        Assert.Equal(18f, read[2].Height);
        Assert.Equal(1, read[2].Direction);
        Assert.Equal(new Vector3(1, 0, 0), read[3].Vertices[1]);
        Assert.Equal(new[] { 0, 1, 2 }, read[3].Indices);
    }

    [Fact]
    public void RoundTrips_EmptyList()
    {
        using var ms = new MemoryStream();
        ColliderCache.Write(ms, new List<CachedCollider>());
        ms.Position = 0;
        Assert.Empty(ColliderCache.Read(ms));
    }

    private static byte[] OneSphere()
    {
        using var ms = new MemoryStream();
        ColliderCache.Write(ms, new List<CachedCollider> { CachedCollider.Sphere(Pose, Vector3.Zero, 1f) });
        return ms.ToArray();
    }

    // Without a header the count read as whatever the first four bytes happened to be, so a truncated file
    // was indistinguishable from a whole one and the reader ran off the end.
    [Fact]
    public void Read_BadMagic_Throws()
    {
        using var ms = new MemoryStream(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 });
        Assert.Throws<InvalidDataException>(() => ColliderCache.Read(ms));
    }

    [Fact]
    public void Read_LegacyHeaderlessFile_IsRejectedRatherThanMisparsed()
    {
        // Exactly what the old writer produced for a single sphere: a bare count, no magic.
        using var legacy = new MemoryStream();
        var w = new BinaryWriter(legacy);
        w.Write(1);
        w.Write((byte)EColliderKind.Sphere);
        legacy.Position = 0;

        Assert.Throws<InvalidDataException>(() => ColliderCache.Read(legacy));
    }

    [Fact]
    public void Read_NegativeCount_Throws()
    {
        byte[] bytes = OneSphere();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), -1);

        using var ms = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => ColliderCache.Read(ms));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(9)]
    public void Read_TruncatedAtAnyPrefix_ThrowsADeclaredParseFailure(int keep)
    {
        byte[] truncated = OneSphere()[..keep];

        using var ms = new MemoryStream(truncated);
        Exception e = Record.Exception(() => ColliderCache.Read(ms));

        Assert.NotNull(e);
        Assert.True(e is EndOfStreamException or InvalidDataException,
            $"a truncated cache should fail as a parse error, got {e.GetType().Name}");
    }

    [Fact]
    public void IsCurrent_TellsAWrittenFileFromALegacyOrMissingOne()
    {
        using var dir = new Helpers.TempDir();
        string written = dir.Write("a.collider", OneSphere());
        string legacy = dir.Write("b.collider", new byte[] { 1, 0, 0, 0 });
        string tooShort = dir.Write("c.collider", new byte[] { 1, 2 });

        Assert.True(ColliderCache.IsCurrent(written));
        Assert.False(ColliderCache.IsCurrent(legacy));
        Assert.False(ColliderCache.IsCurrent(tooShort));
        Assert.False(ColliderCache.IsCurrent(Path.Combine(dir.Path, "absent.collider")));
    }
}
