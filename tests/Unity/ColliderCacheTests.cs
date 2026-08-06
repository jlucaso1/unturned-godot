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

    // A ladder's climbing volume travels with the colliders and must survive the round trip on any kind:
    // it is what decides, at load, whether a box becomes solid geometry or a body the climb probe finds.
    [Fact]
    public void RoundTrips_TheLadderFlag()
    {
        var colliders = new List<CachedCollider>
        {
            CachedCollider.Box(Pose, Vector3.Zero, new Vector3(1.15f, 0.15f, 6.75f), isLadder: true),
            CachedCollider.Box(Pose, Vector3.Zero, Vector3.One),
            CachedCollider.Sphere(Pose, Vector3.Zero, 1f, isLadder: true),
            CachedCollider.Capsule(Pose, Vector3.Zero, 0.5f, 2f, 1, isLadder: true),
            CachedCollider.Mesh(Pose, new[] { Vector3.Zero }, new[] { 0 }, isLadder: true),
        };

        using var ms = new MemoryStream();
        ColliderCache.Write(ms, colliders);
        ms.Position = 0;
        List<CachedCollider> read = ColliderCache.Read(ms);

        Assert.Equal(new[] { true, false, true, true, true },
            read.ConvertAll(c => c.IsLadder).ToArray());
        // The rest of the collider still reads back around the new flag.
        Assert.Equal(new Vector3(1.15f, 0.15f, 6.75f), read[0].Size);
        Assert.Equal(2f, read[3].Height);
    }

    // The physics material is what an impact resolves its sound and its mark through, so it has to survive
    // the round trip on every kind — and the EMPTY name has to survive as empty, because that is the
    // original's null sharedMaterial and it means "no impact effect" rather than "unknown".
    [Fact]
    public void RoundTrips_ThePhysicsMaterialName()
    {
        var colliders = new List<CachedCollider>
        {
            CachedCollider.Box(Pose, Vector3.Zero, Vector3.One, materialName: "Wood_Static"),
            CachedCollider.Sphere(Pose, Vector3.Zero, 1f),                       // no material
            CachedCollider.Capsule(Pose, Vector3.Zero, 0.5f, 2f, 1, materialName: "Metal_Static"),
            CachedCollider.Mesh(Pose, new[] { Vector3.Zero }, new[] { 0 },
                materialName: "Wood_Static"),                                    // repeats the first
            CachedCollider.Box(Pose, Vector3.Zero, Vector3.One, isLadder: true,
                materialName: "Concrete_Static"),                                // rides beside the flag
        };

        using var ms = new MemoryStream();
        ColliderCache.Write(ms, colliders);
        ms.Position = 0;
        List<CachedCollider> read = ColliderCache.Read(ms);

        Assert.Equal(new[] { "Wood_Static", "", "Metal_Static", "Wood_Static", "Concrete_Static" },
            read.ConvertAll(c => c.MaterialName).ToArray());
        Assert.True(read[4].IsLadder);
        Assert.Equal(2f, read[2].Height);
    }

    // A repeated name is written to the table once, so the file grows by a byte per collider rather than
    // by a string per collider. Ten copies of one name must not cost ten copies of it.
    [Fact]
    public void RepeatedMaterialNames_AreWrittenOnce()
    {
        var many = new List<CachedCollider>();
        for (int i = 0; i < 10; i++)
            many.Add(CachedCollider.Sphere(Pose, Vector3.Zero, 1f, materialName: "Wood_Static"));

        using var repeated = new MemoryStream();
        ColliderCache.Write(repeated, many);

        var bare = new List<CachedCollider>();
        for (int i = 0; i < 10; i++)
            bare.Add(CachedCollider.Sphere(Pose, Vector3.Zero, 1f));
        using var plain = new MemoryStream();
        ColliderCache.Write(plain, bare);

        // One length-prefixed "Wood_Static" (12 bytes) and nothing else.
        Assert.Equal(12, repeated.Length - plain.Length);
    }

    // An index past the table is corruption, but the benign kind: the collider still has its shape, so it
    // reads as "no material" rather than failing the whole file — which would leave the object with no
    // collision at all, a far worse outcome than a silent punch.
    [Fact]
    public void Read_MaterialIndexPastTheTable_ReadsAsNoMaterial()
    {
        using var ms = new MemoryStream();
        ColliderCache.Write(ms, new List<CachedCollider>
        {
            CachedCollider.Sphere(Pose, Vector3.Zero, 1f, materialName: "Wood_Static"),
        });
        byte[] bytes = ms.ToArray();

        // header(8) + count(4) + table count(4) + "Wood_Static"(12) + kind(1) + flags(1) => the material.
        bytes[8 + 4 + 4 + 12 + 2] = 9;

        using var corrupt = new MemoryStream(bytes);
        Assert.Equal(string.Empty, ColliderCache.Read(corrupt)[0].MaterialName);
    }

    [Fact]
    public void Read_ImplausibleMaterialTableCount_FailsWithoutAllocating()
    {
        byte[] bytes = OneSphere();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), int.MaxValue);

        using var ms = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => ColliderCache.Read(ms));
    }

    // "UGC2" files carry no material table at all, so reading one here would take the table count out of
    // the first collider's bytes. The magic is the version: an old file has to read as stale.
    [Fact]
    public void ACacheFromTheUnmaterialedFormatIsStaleRatherThanMisparsed()
    {
        byte[] bytes = OneSphere();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x32434755); // "UGC2"

        using var dir = new Helpers.TempDir();
        Assert.False(ColliderCache.IsCurrent(dir.Write("ugc2.collider", bytes)));
        using var ms = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => ColliderCache.Read(ms));
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

    // The previous format ("UGCL") carried no per-collider flags byte, so reading one of its files with
    // this reader would take the first collider's transform a byte early and misparse everything after
    // it. The magic doubles as the version: an old file has to read as stale and be extracted again.
    [Fact]
    public void ACacheFromThePreviousFormatIsStaleRatherThanMisparsed()
    {
        byte[] bytes = OneSphere();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x4C434755); // "UGCL"

        using var dir = new Helpers.TempDir();
        Assert.False(ColliderCache.IsCurrent(dir.Write("old.collider", bytes)));
        using var ms = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => ColliderCache.Read(ms));
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

    // A magic check alone would call this current: the header is intact and only the payload is missing.
    // The completeness check would then accept it forever and the object would load with no collision,
    // which is exactly the outcome the header was added to prevent.
    [Theory]
    [InlineData(8)]   // header only
    [InlineData(12)]  // header + count
    [InlineData(20)]  // part-way through the first collider
    public void IsCurrent_RejectsAFileTruncatedAfterItsHeader(int keep)
    {
        using var dir = new Helpers.TempDir();
        string path = dir.Write("truncated.collider", OneSphere()[..keep]);

        Assert.False(ColliderCache.IsCurrent(path));
    }

    [Fact]
    public void IsCurrent_RejectsAFileLongerThanItsHeaderDeclares()
    {
        using var dir = new Helpers.TempDir();
        byte[] padded = new byte[OneSphere().Length + 16];
        OneSphere().CopyTo(padded, 0);

        Assert.False(ColliderCache.IsCurrent(dir.Write("padded.collider", padded)));
    }

    // A count corrupted to something plausible-but-huge must not become an allocation: OutOfMemoryException
    // is not a decode failure, so no caller catches it and it would take the load down anyway.
    [Fact]
    public void Read_ImplausibleCount_FailsWithoutAllocating()
    {
        byte[] bytes = OneSphere();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), int.MaxValue);

        using var ms = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => ColliderCache.Read(ms));
    }

    // Bounding the collider count is not enough on its own: a mesh collider's nested vertex and index
    // counts each size an array of their own.
    [Theory]
    [InlineData(true)]   // corrupt the vertex count
    [InlineData(false)]  // corrupt the index count
    public void Read_ImplausibleMeshCount_FailsWithoutAllocating(bool vertices)
    {
        using var ms = new MemoryStream();
        ColliderCache.Write(ms, new List<CachedCollider>
        {
            CachedCollider.Mesh(Pose, new[] { Vector3.Zero, Vector3.One, Vector3.Up }, new[] { 0, 1, 2 }),
        });
        byte[] bytes = ms.ToArray();

        // header(8) + count(4) + material-table count(4) + kind(1) + flags(1) + material(1) +
        // transform(48) = 67; vertex count, then 3 vertices, then indices.
        int offset = vertices ? 67 : 67 + 4 + (3 * 12);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), int.MaxValue);

        using var corrupt = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => ColliderCache.Read(corrupt));
    }

    // The quiet corruption: same length, header still consistent, but the count now says fewer colliders
    // than the payload holds. Every bound passes and Read would just return a short list — no exception,
    // so nothing invalidates the file and the object loads without collision forever.
    [Fact]
    public void Read_CountSmallerThanThePayload_ThrowsRatherThanReturningAShortList()
    {
        using var ms = new MemoryStream();
        ColliderCache.Write(ms, new List<CachedCollider>
        {
            CachedCollider.Sphere(Pose, Vector3.Zero, 1f),
            CachedCollider.Sphere(Pose, Vector3.One, 2f),
        });
        byte[] bytes = ms.ToArray();

        // Header(8) then the count: claim one collider where the payload carries two.
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 1);

        using var corrupt = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => ColliderCache.Read(corrupt));
    }

    [Fact]
    public void Read_ZeroedCount_ThrowsRatherThanReturningNothing()
    {
        byte[] bytes = OneSphere();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 0);

        using var corrupt = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => ColliderCache.Read(corrupt));
    }

    [Fact]
    public void Read_PayloadShorterThanTheHeaderDeclares_Throws()
    {
        byte[] bytes = OneSphere();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length * 4);

        using var ms = new MemoryStream(bytes);
        Assert.Throws<EndOfStreamException>(() => ColliderCache.Read(ms));
    }
}
