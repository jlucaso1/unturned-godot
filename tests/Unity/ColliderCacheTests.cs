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
}
