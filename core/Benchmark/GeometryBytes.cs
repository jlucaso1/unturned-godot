namespace UnturnedGodot.Benchmark;

// Estimates the memory footprint of uploaded geometry from vertex and index counts. Indexing trades
// duplicated vertices for a 32-bit index buffer, so neither the vertex count nor the index count alone
// tells you whether it was a net win — this single number does.
//
// The per-attribute sizes are fixed assumptions, NOT Godot's exact packed vertex layout. That is fine
// for a before/after comparison on the same content: the *direction* of the change (fewer bytes after
// indexing) is robust across any positive stride, which is what the benchmark reports on.
public static class GeometryBytes
{
    public const int VertexAttributeBytes = 24; // position (12) + normal (8) + color (4), approximate
    public const int IndexBytes = 4;            // 32-bit indices

    public static long Estimate(long vertices, long indices) =>
        vertices * VertexAttributeBytes + indices * IndexBytes;
}
