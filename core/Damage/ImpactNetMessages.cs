using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Godot;
using UnturnedGodot.Net;

namespace UnturnedGodot.Damage;

// DamageTool.ReceiveSpawnLegacyImpact on the wire: where a hit landed, which way the surface faces, and
// what that surface is made of.
//
// A surface NAME rather than an index, because the two ends have no shared table to index into — the
// server interns its materials while building collision, and a client that joined a dedicated server never
// built any. Names are short (the longest the game ships is "Peaks_Grass_Dry"), and one datagram per swing
// is nothing beside the per-tick state stream, so this is the cheap way to be robust.
public static class ImpactNetMessages
{
    // The original writes a material name with NAME_LENGTH_BITS = 6, so 63 characters is the game's own
    // ceiling. Kept as a byte-length prefix here, which is the same bound with less bit-fiddling.
    public const int MaxSurfaceBytes = 63;

    // type + player + point (12) + normal (12) + surface length.
    private const int HeaderBytes = 1 + 1 + 12 + 12 + 1;

    public static byte[] WriteImpact(in PunchImpact impact)
    {
        byte[] surface = Encoding.UTF8.GetBytes(impact.Surface ?? string.Empty);
        if (surface.Length > MaxSurfaceBytes)
            surface = surface[..MaxSurfaceBytes];

        var payload = new byte[HeaderBytes + surface.Length];
        payload[0] = (byte)ENetMessage.Impact;
        payload[1] = impact.PlayerId;
        WriteVector3(payload.AsSpan(2), impact.Point);
        WriteVector3(payload.AsSpan(14), impact.Normal);
        payload[26] = (byte)surface.Length;
        surface.CopyTo(payload, HeaderBytes);
        return payload;
    }

    // Throws on a payload that does not carry what its header declares, which is what MalformedPacket
    // turns into a dropped datagram rather than a downed client.
    public static PunchImpact ReadImpact(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length < HeaderBytes)
            throw new EndOfStreamException("An Impact payload is shorter than its header.");

        byte playerId = payload[1];
        Vector3 point = ReadVector3(payload.AsSpan(2));
        Vector3 normal = ReadVector3(payload.AsSpan(14));
        int length = payload[26];
        if (payload.Length - HeaderBytes < length)
            throw new EndOfStreamException("An Impact payload declares more surface than it carries.");

        return new PunchImpact(playerId, point, normal,
            Encoding.UTF8.GetString(payload, HeaderBytes, length));
    }

    private static void WriteVector3(Span<byte> destination, Vector3 value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(destination, value.X);
        BinaryPrimitives.WriteSingleLittleEndian(destination[4..], value.Y);
        BinaryPrimitives.WriteSingleLittleEndian(destination[8..], value.Z);
    }

    private static Vector3 ReadVector3(ReadOnlySpan<byte> source) => new(
        BinaryPrimitives.ReadSingleLittleEndian(source),
        BinaryPrimitives.ReadSingleLittleEndian(source[4..]),
        BinaryPrimitives.ReadSingleLittleEndian(source[8..]));
}
