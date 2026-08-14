using System.Collections.Generic;
using System.Globalization;

namespace UnturnedGodot.Net;

// The netcode's numbers, formatted. Engine-free and returning plain strings, for the same reason the
// console registry is: what a reading SAYS is decided here, where a test can drive it without a window,
// and the game half only has to find the session and print the lines.
//
// Two audiences, one source. `net.stats` wants the whole picture in a few lines of scrollback; the F3
// HUD wants one line that fits beside the frame time. Both come from here so they cannot disagree.
public static class NetReport
{
    // How many message types the breakdown names before it stops. Four covers the shape of every
    // session this protocol produces — the state stream, the zombie stream, inputs, and whichever
    // reliable message is currently the big one — and keeps the report inside a glance.
    public const int TopTypes = 4;

    // Bytes as a person reads them. Deliberately not a "1.0 KB" floor: a solo session's control traffic
    // is genuinely tens of bytes per second, and rounding that to 0.0 KB would hide the one reading that
    // proves the instrument is live.
    public static string Bytes(double bytes) => bytes switch
    {
        >= 1024 * 1024 => (bytes / (1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " MB",
        >= 1024 => (bytes / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString("0", CultureInfo.InvariantCulture) + " B",
    };

    // A round trip that has not happened yet prints as "--" rather than as 0 ms: no measurement and a
    // zero-latency link are different statements, and only one of them is ever true.
    public static string Ping(double roundTripSeconds) =>
        double.IsNaN(roundTripSeconds)
            ? "-- ms"
            : (roundTripSeconds * 1000.0).ToString("0", CultureInfo.InvariantCulture) + " ms";

    // The one line the HUD carries. Client-side numbers, because that is whose frame the HUD is drawn
    // in — a listen server's host reads its server totals with `net.stats`.
    public static string HudLine(NetTraffic? client, double roundTripSeconds) =>
        client == null
            ? "Net  offline"
            : string.Format(CultureInfo.InvariantCulture,
                "Net  ping {0}   up {1}/s ({2:0} dg/s)   down {3}/s ({4:0} dg/s)",
                Ping(roundTripSeconds),
                Bytes(client.SentBytesPerSecond), client.SentDatagramsPerSecond,
                Bytes(client.ReceivedBytesPerSecond), client.ReceivedDatagramsPerSecond);

    // The full reading. `server` and `client` are each optional because all three session shapes are
    // real: a dedicated server has no client, a joined player has no server, and singleplayer has both.
    public static IReadOnlyList<string> Stats(NetServer? server, NetClient? client)
    {
        var lines = new List<string>();
        if (server == null && client == null)
        {
            lines.Add("No session: nothing is connected, hosting or joining.");
            return lines;
        }

        if (server != null)
        {
            NetTraffic traffic = server.Traffic;
            lines.Add(string.Format(CultureInfo.InvariantCulture,
                "server  {0} player(s)  tick {1}   up {2}/s ({3:0} dg/s)   down {4}/s ({5:0} dg/s)",
                server.PlayerCount, server.Tick,
                Bytes(traffic.SentBytesPerSecond), traffic.SentDatagramsPerSecond,
                Bytes(traffic.ReceivedBytesPerSecond), traffic.ReceivedDatagramsPerSecond));
            lines.Add(string.Format(CultureInfo.InvariantCulture,
                "server  total up {0}  down {1}   ({2} / {3} datagrams)",
                Bytes(traffic.SentBytes), Bytes(traffic.ReceivedBytes),
                traffic.SentDatagrams, traffic.ReceivedDatagrams));

            // The counters that were only ever asserted on in tests. Every one of them means
            // "something on this link is not what the protocol expects", and a session where they are
            // all zero is the only session whose other numbers can be read at face value.
            lines.Add(string.Format(CultureInfo.InvariantCulture,
                "server  malformed {0}  oversized {1}  refused-connections {2}  refused-sends {3}  "
                + "rejected-positions {4}  unauthenticated-inputs {5}",
                server.MalformedPacketsDropped, traffic.OversizedDropped, traffic.RefusedConnections,
                traffic.RefusedSends, server.RejectedPositions,
                server.UnauthenticatedInputsDropped));
            lines.Add("server  " + Breakdown(traffic));
        }

        if (client != null)
        {
            NetTraffic traffic = client.Traffic;
            lines.Add(string.Format(CultureInfo.InvariantCulture,
                "client  ping {0}   up {1}/s ({2:0} dg/s)   down {3}/s ({4:0} dg/s)   malformed {5}",
                Ping(client.RoundTripSeconds),
                Bytes(traffic.SentBytesPerSecond), traffic.SentDatagramsPerSecond,
                Bytes(traffic.ReceivedBytesPerSecond), traffic.ReceivedDatagramsPerSecond,
                client.MalformedPacketsDropped));
            lines.Add("client  " + Breakdown(traffic));
        }

        return lines;
    }

    // Where the sent bytes went, by message type. Sent rather than received because that is the side a
    // host controls and the side every one of these findings is about — a snapshot stream nobody needed
    // to receive was still a snapshot stream somebody chose to write.
    public static string Breakdown(NetTraffic traffic)
    {
        System.ArgumentNullException.ThrowIfNull(traffic);
        System.Span<int> top = stackalloc int[TopTypes];
        traffic.TopSentTypes(top);

        var text = new System.Text.StringBuilder("sent by type: ");
        bool any = false;
        for (int i = 0; i < top.Length; i++)
        {
            if (top[i] < 0)
                continue;
            if (any)
                text.Append("   ");
            any = true;
            text.Append(NetTraffic.NameOf(top[i]))
                .Append(' ')
                .Append(Bytes(traffic.SentBytesAt(top[i])));
        }

        return any ? text.ToString() : "sent by type: nothing yet";
    }
}
