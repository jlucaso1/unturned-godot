using System;

namespace UnturnedGodot.Net;

// What the netcode costs, measured rather than argued about.
//
// This project's stated order is parity first, then performance, and the console exists so a frame time
// can be A/B'd between two frames of one session. Every other subsystem had an instrument; the netcode
// had none. Its drop counters existed only for tests to assert on, nothing anywhere counted a byte or a
// datagram, and the interpolation delay was a fixed 0.1 s over a link whose round trip was never measured.
// A claim like "the state stream is O(N^2)" is a code reading until something prints bytes per second.
//
// So: totals and a rate, split by ENetMessage, on both directions, held per connection and rolled up to
// the transport that owns them. Split by message type because that is the axis every netcode decision
// turns on — "the roster is the big one", "snapshots dominate at population" — and an undifferentiated
// bytes/s answers none of it.
//
// Engine-free and time-as-a-parameter, like the rest of core/Net: the owner calls Update(now) and the
// window closes deterministically, so the rates are testable without a clock.
public sealed class NetTraffic
{
    // How long a rate window is. One second because that is the unit the number is reported in: a shorter
    // window makes the reading jump with the 12.5 Hz cadence it is measuring, and a longer one stops
    // responding to the thing being A/B'd.
    public const double WindowSeconds = 1.0;

    // One bucket per message type plus one for everything else. A payload arrives from whoever is on the
    // other end of a socket, so its first byte is not necessarily a type this build knows; classifying it
    // as Other keeps a hostile or mismatched peer's bytes counted without indexing past the array.
    public static readonly int TypeCount = Enum.GetValues<ENetMessage>().Length;

    // The bucket an unrecognised first byte lands in — one past the last real type.
    public static int OtherType => TypeCount;

    private readonly long[] _sentBytes;
    private readonly long[] _sentDatagrams;
    private readonly long[] _receivedBytes;
    private readonly long[] _receivedDatagrams;

    // The window's running sums, published into the rates below when it closes.
    private long _windowSentBytes;
    private long _windowSentDatagrams;
    private long _windowReceivedBytes;
    private long _windowReceivedDatagrams;
    private double _windowStart = double.NaN;

    // The parent this instance also reports to, so one Record call updates both the connection's own
    // reading and its server's total. A tree rather than a scan: the alternative is the console walking
    // every live connection on every frame it draws, which is work proportional to the player count on
    // the render thread, to produce a number that was already being computed.
    //
    // Settable because the composite transport of a listen server is built around children that already
    // exist ("open to LAN" adds a UDP listener to the running loopback server), so the link cannot always
    // be made at construction.
    public NetTraffic? Parent { get; set; }

    public NetTraffic(NetTraffic? parent = null)
    {
        Parent = parent;
        _sentBytes = new long[TypeCount + 1];
        _sentDatagrams = new long[TypeCount + 1];
        _receivedBytes = new long[TypeCount + 1];
        _receivedDatagrams = new long[TypeCount + 1];
    }

    // Totals for the whole session, in wire bytes — payload plus whatever framing the transport added,
    // because the question a bandwidth number answers is what went down the link.
    public long SentBytes { get; private set; }
    public long SentDatagrams { get; private set; }
    public long ReceivedBytes { get; private set; }
    public long ReceivedDatagrams { get; private set; }

    // The last CLOSED window's rates. Not a running estimate: a half-finished window would read low for
    // the first fraction of every second and there is no honest way to extrapolate a burst.
    public double SentBytesPerSecond { get; private set; }
    public double SentDatagramsPerSecond { get; private set; }
    public double ReceivedBytesPerSecond { get; private set; }
    public double ReceivedDatagramsPerSecond { get; private set; }

    // The drop counters, gathered here so a caller that wants "what has gone wrong on this link" reads one
    // object instead of four. They live here rather than being mirrored from the classes that raise them:
    // UdpServerTransport.OversizedDropped and ReliableChannel.RefusedSends now read THROUGH this, so the
    // two can never drift apart.
    public long OversizedDropped { get; private set; }
    public long RefusedConnections { get; private set; }
    public long RefusedSends { get; private set; }

    public void CountOversizedDropped() { OversizedDropped++; Parent?.CountOversizedDropped(); }
    public void CountRefusedConnection() { RefusedConnections++; Parent?.CountRefusedConnection(); }
    public void CountRefusedSend() { RefusedSends++; Parent?.CountRefusedSend(); }

    // `wireBytes` is what the transport actually put on the link, which is NOT payload.Length: the
    // reliable channel prefixes one byte of channel and two of sequence, and the loopback prefixes
    // nothing. Passing it in rather than deriving it here is what keeps the number honest across both.
    public void RecordSent(byte[] payload, int wireBytes) =>
        RecordSent(TypeOf(payload), wireBytes);

    public void RecordReceived(byte[] payload, int wireBytes) =>
        RecordReceived(TypeOf(payload), wireBytes);

    // The bucket form, for a caller holding a framed datagram rather than a payload array: a
    // retransmission is bytes on the link that nobody re-serialized, and slicing the payload back out of
    // it purely to be counted would allocate on every resend of every reliable frame.
    public void RecordSent(int bucket, int wireBytes)
    {
        _sentBytes[bucket] += wireBytes;
        _sentDatagrams[bucket]++;
        SentBytes += wireBytes;
        SentDatagrams++;
        _windowSentBytes += wireBytes;
        _windowSentDatagrams++;
        Parent?.RecordSent(bucket, wireBytes);
    }

    public void RecordReceived(int bucket, int wireBytes)
    {
        _receivedBytes[bucket] += wireBytes;
        _receivedDatagrams[bucket]++;
        ReceivedBytes += wireBytes;
        ReceivedDatagrams++;
        _windowReceivedBytes += wireBytes;
        _windowReceivedDatagrams++;
        Parent?.RecordReceived(bucket, wireBytes);
    }

    // An empty payload carries no type byte, and a first byte this build has no name for came from a
    // mismatched or hostile peer. Both are still traffic that crossed the link, so they land in Other
    // rather than vanishing — and neither can index past the array.
    public static int TypeOf(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return payload.Length > 0 && payload[0] < TypeCount ? payload[0] : OtherType;
    }

    // Per-type totals. Indexed by the enum so a caller can name what it wants; the Other bucket is
    // reachable through OtherType.
    public long SentBytesOf(ENetMessage type) => _sentBytes[(int)type];
    public long SentDatagramsOf(ENetMessage type) => _sentDatagrams[(int)type];
    public long ReceivedBytesOf(ENetMessage type) => _receivedBytes[(int)type];
    public long ReceivedDatagramsOf(ENetMessage type) => _receivedDatagrams[(int)type];

    public long SentBytesAt(int bucket) => _sentBytes[bucket];
    public long ReceivedBytesAt(int bucket) => _receivedBytes[bucket];

    // Closes the window if a second has passed. Divided by the ELAPSED time rather than by WindowSeconds:
    // the owner calls this from its own Update, which runs at whatever cadence the host manages, so a
    // window that ran 1.4 s must not be reported as though it ran 1.0.
    public void Update(double now)
    {
        if (double.IsNaN(_windowStart))
        {
            _windowStart = now;
            return;
        }

        double elapsed = now - _windowStart;
        if (elapsed < WindowSeconds)
            return;

        SentBytesPerSecond = _windowSentBytes / elapsed;
        SentDatagramsPerSecond = _windowSentDatagrams / elapsed;
        ReceivedBytesPerSecond = _windowReceivedBytes / elapsed;
        ReceivedDatagramsPerSecond = _windowReceivedDatagrams / elapsed;

        _windowSentBytes = 0;
        _windowSentDatagrams = 0;
        _windowReceivedBytes = 0;
        _windowReceivedDatagrams = 0;
        _windowStart = now;
    }

    // The busiest message types by sent bytes, for a report that has to fit on one console line. Returns
    // the bucket indices, so a caller can print the enum name (or "other" for OtherType) and both the
    // byte and datagram totals it already has.
    public void TopSentTypes(Span<int> into)
    {
        for (int slot = 0; slot < into.Length; slot++)
            into[slot] = -1;
        for (int bucket = 0; bucket <= TypeCount; bucket++)
        {
            if (_sentBytes[bucket] == 0)
                continue;
            // Insertion sort into a span of at most a handful of slots: cheaper than sorting the whole
            // bucket array, and this runs from a console command rather than from a tick.
            for (int slot = 0; slot < into.Length; slot++)
            {
                if (into[slot] >= 0 && _sentBytes[into[slot]] >= _sentBytes[bucket])
                    continue;
                for (int shift = into.Length - 1; shift > slot; shift--)
                    into[shift] = into[shift - 1];
                into[slot] = bucket;
                break;
            }
        }
    }

    // The name of a bucket, for a report. Other is not an ENetMessage, so it cannot come from the enum.
    public static string NameOf(int bucket) =>
        bucket >= TypeCount ? "other" : ((ENetMessage)bucket).ToString();
}
