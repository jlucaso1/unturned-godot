using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Unity;

// Interprets a Unity Mesh object (read via TypeTreeReader) into plain geometry. Handles the common
// path Unturned object models use: uncompressed, inline vertex data, Float32/Float16/UNorm8 channels.
public sealed class UnityMesh
{
    public string Name = string.Empty;
    public Vector3[] Vertices = Array.Empty<Vector3>();
    public Vector3[] Normals = Array.Empty<Vector3>();
    public Vector2[] Uvs = Array.Empty<Vector2>();
    public int[] Indices = Array.Empty<int>();       // all triangle indices flattened
    public List<int[]> Submeshes = new();            // per-submesh triangle indices (parallel to materials)

    // Skinning (empty for non-skinned meshes): 4 bone weights + 4 bone indices per vertex, and one bind
    // pose matrix per bone (Unity column-major, 16 floats). Populated from the BlendWeight/BlendIndices
    // vertex channels and m_BindPose.
    public float[] BoneWeights = Array.Empty<float>();
    public int[] BoneIndices = Array.Empty<int>();
    public List<float[]> BindPoses = new();
    public const int BonesPerVertex = 4;

    // False when the mesh uses compression or external stream data we don't decode (caller falls back).
    public bool Usable { get; private set; }

    // Where a mesh keeps its vertex buffer when it is not inline: a byte range in one of the bundle's
    // .resS nodes. Unity moves a mesh's buffer out there on its own schedule, so this is not a property of
    // the model — a vehicle's Wheel_LOD0 is streamed while the Wheel_LOD1 beside it is inline. A caller
    // that can read the range hands the bytes back to Read; one that cannot gets an unusable mesh, which
    // is what this reader did for every streamed mesh before.
    public readonly record struct StreamRef(string Path, long Offset, int Size)
    {
        // `Path` is null on a default instance, which is what ReadStreamRef hands back for a mesh that is
        // not streamed at all — the pattern rather than a .Length keeps that from faulting here.
        public bool IsStreamed => Path is { Length: > 0 } && Size > 0;

        // The last segment of "archive:/CAB-x/CAB-x.resS" is the bundle node's file name, which is how
        // BundlePass addresses it. Same rule UnityTexture.StreamFileName follows.
        public string FileName
        {
            get
            {
                string path = Path ?? string.Empty;
                int slash = path.LastIndexOf('/');
                return slash >= 0 ? path[(slash + 1)..] : path;
            }
        }
    }

    // The mesh's external vertex buffer, without decoding anything: this is what a caller plans its
    // .resS pass from, before it has the bytes to call Read with.
    public static StreamRef ReadStreamRef(Dictionary<string, object> mesh)
    {
        if (ToInt(mesh["m_MeshCompression"]) != 0)
            return default; // a compressed mesh carries its geometry inline, whatever m_StreamData says
        if (mesh["m_StreamData"] is not Dictionary<string, object> stream)
            return default;
        return new StreamRef((string)stream["path"], Convert.ToInt64(stream["offset"]),
            Convert.ToInt32(stream["size"]));
    }

    private static readonly int[] FormatSize =
    {
        4, 2, 1, 1, 2, 2, 1, 1, 2, 2, 4, 4, // 0..11: Float32,Float16,UNorm8,SNorm8,UNorm16,SNorm16,UInt8,SInt8,UInt16,SInt16,UInt32,SInt32
    };

    public static UnityMesh Read(Dictionary<string, object> mesh) => Read(mesh, null);

    // `streamVertexData` is the mesh's external vertex buffer, when the caller was able to read the range
    // ReadStreamRef named. The layout inside it is identical to the inline one — the channels, strides and
    // stream offsets all still apply — so it simply takes the place of m_VertexData's own bytes.
    public static UnityMesh Read(Dictionary<string, object> mesh, byte[]? streamVertexData)
    {
        var result = new UnityMesh { Name = mesh.TryGetValue("m_Name", out object? n) ? (string)n : string.Empty };

        // Quantized geometry lives in m_CompressedMesh instead of the vertex buffers. The game's own
        // bundle never uses it; workshop mods do, so read it rather than dropping the mesh.
        if (ToInt(mesh["m_MeshCompression"]) != 0)
            return ReadCompressed(mesh, result);

        var streamData = (Dictionary<string, object>)mesh["m_StreamData"];
        bool streamed = ((string)streamData["path"]).Length != 0;
        if (streamed && streamVertexData == null)
            return result; // vertex data lives in an external .resS the caller could not read

        var vertexData = (Dictionary<string, object>)mesh["m_VertexData"];
        int vertexCount = ToInt(vertexData["m_VertexCount"]);
        var channels = (List<object>)vertexData["m_Channels"];
        // A streamed mesh writes an empty m_DataSize; the buffer is the range out of the .resS instead.
        byte[] buffer = streamed ? streamVertexData! : (byte[])vertexData["m_DataSize"];

        // Every stride below is computed from the channels' declared formats, so a format the header names
        // but Unity does not define would index FormatSize out of bounds and fault the whole bundle. An
        // undecodable mesh is what Usable already says, so say it here instead.
        if (!KnownFormats(channels))
            return result;

        int[] strides = ComputeStreamStrides(channels, out int[] streamOffsets, vertexCount);
        // The channel readers index the buffer straight from the strides, so a buffer shorter than the
        // header describes would fault rather than decode. That was unreachable while the bytes always
        // came from the same object; a range read out of a .resS can fall short (a truncated stream node,
        // a mismatched offset), and a mesh nobody can decode is exactly what Usable already means.
        if (!FitsChannels(buffer, vertexCount, strides, streamOffsets))
            return result;

        result.Vertices = ReadChannel(channels, 0, buffer, vertexCount, strides, streamOffsets);
        result.Normals = ReadChannel(channels, 1, buffer, vertexCount, strides, streamOffsets);
        result.Uvs = ReadUvChannel(channels, 4, buffer, vertexCount, strides, streamOffsets); // UV0
        result.BoneWeights = ReadFloat4Channel(channels, 12, buffer, vertexCount, strides, streamOffsets);
        result.BoneIndices = ReadInt4Channel(channels, 13, buffer, vertexCount, strides, streamOffsets);
        result.BindPoses = ReadBindPoses(mesh);
        result.Submeshes = ReadSubmeshes(mesh, result.Vertices.Length);
        result.Indices = Flatten(result.Submeshes);
        result.Usable = result.Vertices.Length > 0 && result.Indices.Length > 0;
        return result;
    }

    // The submesh index arrays as one buffer, in a single pass. The old running Concat reallocated and
    // recopied the whole growing array once per submesh — O(submeshes * total indices), quadratic in
    // submesh count.
    private static int[] Flatten(List<int[]> submeshes)
    {
        int total = 0;
        foreach (int[] sm in submeshes)
            total += sm.Length;

        var flat = new int[total];
        int offset = 0;
        foreach (int[] sm in submeshes)
        {
            Array.Copy(sm, 0, flat, offset, sm.Length);
            offset += sm.Length;
        }
        return flat;
    }

    // A compressed mesh carries its triangles as one packed run; the submesh table still says which slice
    // of that run belongs to each material, so the split is taken from the file rather than assumed.
    private static UnityMesh ReadCompressed(Dictionary<string, object> mesh, UnityMesh result)
    {
        CompressedMesh compressed = CompressedMesh.Read(
            mesh.TryGetValue("m_CompressedMesh", out object? node) ? node : null);
        if (!compressed.HasGeometry)
            return result;

        result.Vertices = compressed.Vertices;
        result.Normals = compressed.Normals;
        result.Uvs = compressed.Uvs;
        result.BoneWeights = compressed.BoneWeights;
        result.BoneIndices = compressed.BoneIndices;
        result.BindPoses = ReadBindPoses(mesh);
        result.Submeshes = SliceSubmeshes(mesh, compressed.Triangles, result.Vertices.Length);
        // Taken from the submeshes rather than from compressed.Triangles, so this path and the
        // uncompressed one agree about what Indices holds: the indices that survived validation. Handing
        // back the raw run would have put the very values SliceSubmeshes just rejected back in reach.
        result.Indices = Flatten(result.Submeshes);
        result.Usable = result.Indices.Length > 0;
        return result;
    }

    // firstByte/indexCount address the index buffer the mesh would have had, so the element size the
    // header declares converts them into offsets in the unpacked triangle list.
    private static List<int[]> SliceSubmeshes(Dictionary<string, object> mesh, int[] triangles,
        int vertexCount)
    {
        int indexSize = ToInt(mesh["m_IndexFormat"]) == 1 ? 4 : 2;
        var result = new List<int[]>();

        foreach (object s in (List<object>)mesh["m_SubMeshes"])
        {
            var sm = (Dictionary<string, object>)s;
            int first = ToInt(sm["firstByte"]) / indexSize;
            int count = ToInt(sm["indexCount"]);
            if (ToInt(sm["topology"]) != 0 || first < 0 || count <= 0 || first + count > triangles.Length)
            {
                result.Add(Array.Empty<int>()); // keep index alignment with the material list
                continue;
            }

            var slice = new int[count];
            Array.Copy(triangles, first, slice, 0, count);
            // Same rule as the uncompressed path: an index that names a vertex the mesh does not have is
            // not renderable, and passing it on hands the fault to whoever draws it rather than to the
            // reader that can still say no.
            result.Add(ApplyBaseVertex(slice, BaseVertex(sm), vertexCount) ? slice : Array.Empty<int>());
        }

        // A mesh with no usable submesh table still renders as one surface — validated like any other,
        // since nothing about an absent table makes the packed triangles trustworthy.
        if (result.Count == 0)
            result.Add(ApplyBaseVertex(triangles, 0, vertexCount) ? triangles : Array.Empty<int>());

        return result;
    }

    // A channel's component count. Unity packs it into the low nibble of `dimension` and keeps the high
    // one for flags, so the field is not the number it looks like: a normals channel written as 52 is four
    // Float16 components with 0x3 set above them. Read whole, that channel claimed 52 components and
    // pushed the stream's stride from 32 bytes to 116 — every vertex then read from the wrong offset, and
    // the vertex buffer looked far too short for the vertex count, which is what made the mesh unusable.
    private static int Dimension(Dictionary<string, object> channel) => ToInt(channel["dimension"]) & 0x0F;

    // Whether the buffer holds every vertex of every stream the header declares.
    private static bool FitsChannels(byte[] buffer, int vertexCount, int[] strides, int[] streamOffsets)
    {
        for (int s = 0; s < strides.Length; s++)
        {
            long end = (long)streamOffsets[s] + (long)vertexCount * strides[s];
            if (end > buffer.Length)
                return false;
        }
        return true;
    }

    private static int[] ComputeStreamStrides(List<object> channels, out int[] streamOffsets, int vertexCount)
    {
        int streamCount = 1;
        foreach (object c in channels)
        {
            var ch = (Dictionary<string, object>)c;
            if (Dimension(ch) > 0)
                streamCount = Math.Max(streamCount, ToInt(ch["stream"]) + 1);
        }

        var strides = new int[streamCount];
        foreach (object c in channels)
        {
            var ch = (Dictionary<string, object>)c;
            int dim = Dimension(ch);
            if (dim == 0)
                continue;
            int stream = ToInt(ch["stream"]);
            int end = ToInt(ch["offset"]) + dim * FormatSize[ToInt(ch["format"])];
            strides[stream] = Math.Max(strides[stream], end);
        }

        streamOffsets = new int[streamCount];
        for (int s = 1; s < streamCount; s++)
        {
            int prev = streamOffsets[s - 1] + vertexCount * strides[s - 1];
            streamOffsets[s] = (prev + 15) & ~15; // streams align to 16 bytes
        }
        return strides;
    }

    private static Vector3[] ReadChannel(List<object> channels, int index, byte[] buffer,
        int vertexCount, int[] strides, int[] streamOffsets)
    {
        if (index >= channels.Count)
            return Array.Empty<Vector3>();

        var ch = (Dictionary<string, object>)channels[index];
        int dim = Dimension(ch);
        int format = ToInt(ch["format"]);
        if (dim < 3 || !IsFloatFormat(format))
            return Array.Empty<Vector3>();

        int stream = ToInt(ch["stream"]);
        int stride = strides[stream];
        int baseOffset = streamOffsets[stream] + ToInt(ch["offset"]);
        int componentSize = FormatSize[format];

        var values = new Vector3[vertexCount];
        for (int v = 0; v < vertexCount; v++)
        {
            int p = baseOffset + v * stride;
            values[v] = new Vector3(
                ReadComponent(buffer, p, format),
                ReadComponent(buffer, p + componentSize, format),
                ReadComponent(buffer, p + 2 * componentSize, format));
        }
        return values;
    }

    private static Vector2[] ReadUvChannel(List<object> channels, int index, byte[] buffer,
        int vertexCount, int[] strides, int[] streamOffsets)
    {
        if (index >= channels.Count)
            return Array.Empty<Vector2>();

        var ch = (Dictionary<string, object>)channels[index];
        int dim = Dimension(ch);
        int format = ToInt(ch["format"]);
        if (dim < 2 || !IsFloatFormat(format))
            return Array.Empty<Vector2>();

        int stream = ToInt(ch["stream"]);
        int stride = strides[stream];
        int baseOffset = streamOffsets[stream] + ToInt(ch["offset"]);
        int componentSize = FormatSize[format];

        var values = new Vector2[vertexCount];
        for (int v = 0; v < vertexCount; v++)
        {
            int p = baseOffset + v * stride;
            values[v] = new Vector2(ReadComponent(buffer, p, format), ReadComponent(buffer, p + componentSize, format));
        }
        return values;
    }

    // Bone weights (BlendWeight channel), flattened to four per vertex; empty when absent.
    //
    // The channel's own dimension is how many influences each vertex carries, and it is NOT always four.
    // Unity writes as many as the mesh's import settings asked for, and the rest of the slot is simply not
    // in the buffer — a two-influence mesh has a 16-byte skin stream where a four-influence one has 32.
    // Requiring four dropped every such mesh's skin on the floor, and the game ships two of them in
    // resources.assets: the first-person Viewmodel arms (464 vertices, 16 bind poses) and one more rig
    // beside them, both authored at two influences. That is the whole of the README's "skin weights the
    // port does not decode yet" — the data is inline and uncompressed, it is just two wide.
    //
    // The unread slots stay zero, which is what they mean: Unity normalizes a vertex's weights across the
    // influences it declares, so two that sum to 1 are complete on their own.
    private static float[] ReadFloat4Channel(List<object> channels, int index, byte[] buffer,
        int vertexCount, int[] strides, int[] streamOffsets)
    {
        if (index >= channels.Count)
            return Array.Empty<float>();
        var ch = (Dictionary<string, object>)channels[index];
        int format = ToInt(ch["format"]);
        int dim = Dimension(ch);
        if (dim < 1 || !IsFloatFormat(format))
            return Array.Empty<float>();

        int stream = ToInt(ch["stream"]);
        int stride = strides[stream];
        int baseOffset = streamOffsets[stream] + ToInt(ch["offset"]);
        int size = FormatSize[format];
        int influences = Math.Min(dim, BonesPerVertex);

        var values = new float[vertexCount * BonesPerVertex];
        for (int v = 0; v < vertexCount; v++)
        {
            int p = baseOffset + v * stride;
            for (int c = 0; c < influences; c++)
                values[(v * BonesPerVertex) + c] = ReadComponent(buffer, p + (c * size), format);
        }
        return values;
    }

    // Bone indices (BlendIndices channel; UInt8/16/32), flattened to four per vertex; empty when absent.
    // Same dimension rule as the weights above — the two channels always agree about how many influences
    // a vertex has, so an index whose weight was not written stays 0 and contributes nothing.
    private static int[] ReadInt4Channel(List<object> channels, int index, byte[] buffer,
        int vertexCount, int[] strides, int[] streamOffsets)
    {
        if (index >= channels.Count)
            return Array.Empty<int>();
        var ch = (Dictionary<string, object>)channels[index];
        int format = ToInt(ch["format"]);
        int dim = Dimension(ch);
        // The mirror of the float channels' guard: bone ids are integers, and a float format here would
        // be read at the wrong width by ReadIntComponent.
        if (dim < 1 || IsFloatFormat(format))
            return Array.Empty<int>();

        int stream = ToInt(ch["stream"]);
        int stride = strides[stream];
        int baseOffset = streamOffsets[stream] + ToInt(ch["offset"]);
        int size = FormatSize[format];
        int influences = Math.Min(dim, BonesPerVertex);

        var values = new int[vertexCount * BonesPerVertex];
        for (int v = 0; v < vertexCount; v++)
        {
            int p = baseOffset + v * stride;
            for (int c = 0; c < influences; c++)
                values[(v * BonesPerVertex) + c] = ReadIntComponent(buffer, p + (c * size), format);
        }
        return values;
    }

    // The bone-index channel's integer formats. The signed narrow widths are listed for the same reason
    // ReadComponent lists them: FormatSize already sizes them, so a 4-byte read at a 1- or 2-byte stride
    // silently mixes in the neighbouring component instead of failing.
    private static int ReadIntComponent(byte[] buffer, int offset, int format) => format switch
    {
        6 => buffer[offset],                            // UInt8
        7 => (sbyte)buffer[offset],                     // SInt8
        8 => BitConverter.ToUInt16(buffer, offset),     // UInt16
        9 => BitConverter.ToInt16(buffer, offset),      // SInt16
        _ => BitConverter.ToInt32(buffer, offset),      // UInt32/SInt32 (format 10/11)
    };

    // One bind pose matrix (16 floats, Unity column-major e00..e33) per bone.
    private static List<float[]> ReadBindPoses(Dictionary<string, object> mesh)
    {
        var result = new List<float[]>();
        if (!mesh.TryGetValue("m_BindPose", out object? bp) || bp is not List<object> poses)
            return result;

        foreach (object pose in poses)
        {
            var m = (Dictionary<string, object>)pose;
            var matrix = new float[16];
            for (int col = 0; col < 4; col++)
                for (int row = 0; row < 4; row++)
                    matrix[col * 4 + row] = Convert.ToSingle(m[$"e{row}{col}"]); // column-major
            result.Add(matrix);
        }
        return result;
    }

    // Unity's six floating-point VertexFormats, decoded at the width FormatSize already declares.
    //
    // Only 0/1/2 used to be decoded and 3/4/5 fell through to a 4-byte BitConverter.ToSingle. That was
    // worse than not reading the mesh at all: FormatSize gets their widths right, so the strides and the
    // buffer-fits test both pass, the mesh reports Usable, and what comes out is a scrambled model rather
    // than a rejected one. The normalized readings are Unity's own — a signed value divides by its
    // positive maximum, which leaves the most negative value a hair below -1, so it clamps.
    private static float ReadComponent(byte[] buffer, int offset, int format) => format switch
    {
        0 => BitConverter.ToSingle(buffer, offset),                        // Float32
        1 => (float)BitConverter.ToHalf(buffer, offset),                   // Float16
        2 => buffer[offset] / 255f,                                        // UNorm8
        3 => Math.Max((sbyte)buffer[offset] / 127f, -1f),                  // SNorm8
        4 => BitConverter.ToUInt16(buffer, offset) / 65535f,               // UNorm16
        _ => Math.Max(BitConverter.ToInt16(buffer, offset) / 32767f, -1f), // SNorm16 (format 5)
    };

    // Whether a format carries a floating-point quantity, which is what positions, normals, UVs and blend
    // weights are. The integer formats (6..11) hold ids and counts; Unity reads them through a separate
    // path and so does this reader (ReadIntComponent, for the bone-index channel). A float channel that
    // names one is not something to guess at — reinterpreting its bytes as a float is exactly the silent
    // scrambling above — so the channel reads as absent, and for positions that makes the mesh unusable,
    // which is the answer Usable exists to give.
    private static bool IsFloatFormat(int format) => format is >= 0 and <= 5;

    // A component's byte size, or -1 for a format outside Unity's VertexFormat enum. Indexing FormatSize
    // directly threw on such a header, which fails the whole bundle rather than the one mesh that named it.
    private static int FormatSizeOf(int format) =>
        (uint)format < (uint)FormatSize.Length ? FormatSize[format] : -1;

    // Whether every channel that carries data names a format this reader can size and decode.
    private static bool KnownFormats(List<object> channels)
    {
        foreach (object c in channels)
        {
            var ch = (Dictionary<string, object>)c;
            if (Dimension(ch) > 0 && FormatSizeOf(ToInt(ch["format"])) < 0)
                return false;
        }
        return true;
    }

    // One index array per submesh (triangle lists only), parallel to the palette's materials.
    //
    // Two separate bounds hold here, and neither is optional. `firstByte`/`indexCount` come straight off
    // the TypeTree, so a range that overruns m_IndexBuffer threw out of BitConverter and turned every
    // object in that bundle into a box. The worse one is silent: a range that fits the index buffer can
    // still hold values naming vertices this mesh does not have, and nothing downstream re-checks them —
    // they go into the cache through MeshCache.Write and are handed to ImporterMesh.AddSurface on the main
    // thread on every warm load for the life of that cache entry. A submesh that fails either test is
    // dropped to an empty array, the same answer SliceSubmeshes gives, which keeps the list aligned with
    // the material palette.
    private static List<int[]> ReadSubmeshes(Dictionary<string, object> mesh, int vertexCount)
    {
        byte[] indexBuffer = (byte[])mesh["m_IndexBuffer"];
        bool is32 = ToInt(mesh["m_IndexFormat"]) == 1;
        int size = is32 ? 4 : 2;

        var result = new List<int[]>();
        foreach (object s in (List<object>)mesh["m_SubMeshes"])
        {
            var sm = (Dictionary<string, object>)s;
            if (ToInt(sm["topology"]) != 0)
            {
                result.Add(Array.Empty<int>()); // keep index alignment with materials
                continue;
            }

            int firstByte = ToInt(sm["firstByte"]);
            int indexCount = ToInt(sm["indexCount"]);
            // Widened to long deliberately: indexCount * size overflows int for a large enough claimed
            // count, and an overflowed product compares as "fits" against a buffer it does not fit.
            if (firstByte < 0 || indexCount <= 0
                || (long)indexCount * size > (long)indexBuffer.Length - firstByte)
            {
                result.Add(Array.Empty<int>());
                continue;
            }

            int baseVertex = BaseVertex(sm);
            var indices = new int[indexCount];
            for (int i = 0; i < indexCount; i++)
            {
                int p = firstByte + i * size;
                // Index buffer values are relative to the submesh's baseVertex, which is zero for nearly
                // every mesh in the game's own bundle but not for one Unity split: a mesh with more than
                // 65 535 vertices keeps 16-bit indices and moves the window with baseVertex instead.
                indices[i] = is32
                    ? unchecked((int)BitConverter.ToUInt32(indexBuffer, p))
                    : BitConverter.ToUInt16(indexBuffer, p);
            }
            result.Add(ApplyBaseVertex(indices, baseVertex, vertexCount) ? indices : Array.Empty<int>());
        }
        return result;
    }

    // m_SubMeshes.baseVertex, absent on the older mesh versions that never wrote it.
    private static int BaseVertex(Dictionary<string, object> submesh) =>
        submesh.TryGetValue("baseVertex", out object? value) ? ToInt(value) : 0;

    // Adds baseVertex to every index in place, and answers whether they all landed on a vertex the mesh
    // actually has. The sum is taken in long because a 32-bit index buffer holds values up to uint.MaxValue
    // — read back as int those are negative, and adding baseVertex to one could wrap right back into the
    // valid range and pass the very test meant to catch it. (Not to be confused with MeshIndices.Rebase,
    // which moves an already-valid part's indices into a combined mesh's pool.)
    private static bool ApplyBaseVertex(int[] indices, int baseVertex, int vertexCount)
    {
        for (int i = 0; i < indices.Length; i++)
        {
            long index = (uint)indices[i] + (long)baseVertex;
            if (index < 0 || index >= vertexCount)
                return false;
            indices[i] = (int)index;
        }
        return true;
    }

    private static int ToInt(object value) => Convert.ToInt32(value);
}
