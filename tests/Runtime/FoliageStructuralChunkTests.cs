using Chickensoft.GoDotTest;
using Godot;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The one piece of src/World that is not the streaming node itself: the record the structural benchmark
// hands back, pairing a built mesh with what it cost to build.
//
// It is in this suite rather than the xUnit one for a single reason — it carries an ArrayMesh, and an
// ArrayMesh is a Godot resource. Constructing one outside the engine is not possible, which is the whole
// reason src/ needs a suite of its own.
public class FoliageStructuralChunkTests : TestClass
{
    public FoliageStructuralChunkTests(Node testScene) : base(testScene) { }

    [Test]
    public void ItCarriesItsMeshAndCounts()
    {
        var mesh = new ArrayMesh();
        var chunk = new FoliageStructuralChunk(mesh, Count: 128, OriginSpread: 12.5f);

        Assert.Same(mesh, chunk.Mesh);
        Assert.Equal(128, chunk.Count);
        Assert.Equal(12.5f, chunk.OriginSpread);
    }

    // Value equality is the reason it is a record struct rather than a tuple with names: two chunks over
    // the same mesh and the same counts are the same chunk, and a benchmark that de-duplicates them
    // depends on that rather than on reference identity.
    [Test]
    public void TwoChunksOverTheSameMeshAreEqual()
    {
        var mesh = new ArrayMesh();

        Assert.Equal(new FoliageStructuralChunk(mesh, 4, 1f), new FoliageStructuralChunk(mesh, 4, 1f));
        Assert.NotEqual(new FoliageStructuralChunk(mesh, 4, 1f), new FoliageStructuralChunk(mesh, 5, 1f));
    }
}
