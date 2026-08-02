using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace UnturnedGodot.Net;

// Every byte a message decoder reads comes off a socket, so it is attacker-controlled: a datagram can
// be truncated mid-field, carry a count that outruns the payload, or hold a string length that no
// longer describes anything. The decoders read straight through a BinaryReader and let those cases
// throw, which is the right call for the decoder — but the receive loop runs inside _PhysicsProcess,
// where an escaping exception takes the whole process down. This is the filter that turns "this
// datagram is nonsense" into "drop it and keep serving", while leaving a genuine bug (a null
// dereference, a bad cast, an invariant violation) free to surface.
public static class MalformedPacket
{
    // True when the exception is one a hostile or corrupt datagram can legitimately provoke.
    // IOException covers EndOfStreamException (a truncated field) and BinaryReader's own "too many
    // bytes in what should have been a 7 bit encoded Int32". ArgumentException covers the decoder
    // fallbacks a non-UTF-8 name hits, and the range checks on a bad length.
    public static bool IsDecodeFailure(Exception e) =>
        e is IOException
            or InvalidDataException
            or ArgumentException
            or IndexOutOfRangeException
            or OverflowException
            or FormatException;

    // Runs one decoder over one payload. False means the bytes did not decode and the caller should drop
    // the datagram; the caller counts its own drops. Pass a cached delegate — a method group converts to
    // a fresh one on every call, and these run per received message.
    //
    // The point of the seam is its narrowness: only `read` is inside the guard, so a fault in whatever
    // the caller does with the result still surfaces as the defect it is.
    public static bool TryDecode<T>(byte[] payload, Func<byte[], T> read, [MaybeNullWhen(false)] out T value)
    {
        try
        {
            value = read(payload);
            return true;
        }
        catch (Exception e) when (IsDecodeFailure(e))
        {
            value = default;
            return false;
        }
    }
}
