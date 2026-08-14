using Godot;
using UnturnedGodot.Net;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Net;

// The RTT probe: the server echoes back the newest input frame number it has heard from a client, and
// the client — which knows when it sent that frame — turns the echo into a round trip.
//
// Worth being explicit about why this is the cheap design. Nothing is timestamped on the wire, so the
// two machines need no shared clock; nothing is trusted, because a client that lies about its own frame
// numbers only misleads itself; and the number it produces is the input an adaptive interpolation delay
// needs, which is the whole reason the fixed 0.1 s could never adapt — the link was never measured.
public class RoundTripTests
{
    private static bool FlatGround(float x, float z, out float y)
    {
        y = 10f;
        return true;
    }

    private static readonly Vector3 Spawn = new(0, 10f, 0);
    private const string Level = "PEI";

    private sealed class Session
    {
        public readonly LoopbackServerTransport Transport = new();
        public readonly NetServer Server;
        public readonly NetClient Client;
        public double Now = 5000.0;

        public Session()
        {
            Server = new NetServer(Transport,
                new ServerSimulation(new HeightfieldMoveSolver(FlatGround)), Spawn, Level);
            Client = new NetClient(Transport.CreateClient(), "Ana", Level);
        }

        public void Pump(int rounds = 1)
        {
            for (int i = 0; i < rounds; i++)
            {
                Now += ServerSimulation.TickRate;
                Server.Update(Now);
                Client.Update(Now);
            }
        }
    }

    [Fact]
    public void BeforeAnyEchoTheRoundTripIsNotAMeasurement()
    {
        var session = new Session();
        session.Pump(4);

        Assert.True(session.Client.Joined);
        // NaN rather than zero, and the report says so: "no reading" and "a zero-latency link" are
        // different statements and only one of them can ever be true.
        Assert.True(double.IsNaN(session.Client.RoundTripSeconds));
        Assert.True(double.IsNaN(session.Client.PingMilliseconds));
    }

    [Fact]
    public void AnEchoOfAnInputFrameBecomesTheRoundTrip()
    {
        var session = new Session();
        session.Pump(4);

        // Sent at T; the loopback delivers within the same pump, so the server echoes it on the next
        // tick and the client reads that echo a tick after — one TickRate of round trip.
        session.Client.SendInput(Input(11), session.Now);
        session.Pump();

        Assert.False(double.IsNaN(session.Client.RoundTripSeconds));
        Assert.Equal(ServerSimulation.TickRate, session.Client.RoundTripSeconds, 4);
    }

    // The server keeps echoing the same frame number until a newer input arrives, so most echoes are
    // repeats. Measuring a repeat would report a round trip inflated by however long the server sat on
    // it — the reading would climb steadily while the player stood still and sent nothing.
    [Fact]
    public void RepeatedEchoesOfOneFrameAreMeasuredOnce()
    {
        var session = new Session();
        session.Pump(4);
        session.Client.SendInput(Input(11), session.Now);
        session.Pump();
        double first = session.Client.RoundTripSeconds;

        session.Pump(10); // ten more ticks of the server repeating frame 11 back at us

        Assert.Equal(first, session.Client.RoundTripSeconds, 6);
    }

    // Reordering is ordinary over UDP. Echoing back an older frame than one already echoed would show
    // up as a round trip that went backwards, so the server tracks the newest frame wrap-safely.
    [Fact]
    public void AReorderedInputDoesNotRewindWhatTheServerEchoes()
    {
        var session = new Session();
        session.Pump(4);

        session.Client.SendInput(Input(20), session.Now);
        session.Pump();
        double afterNewest = session.Client.RoundTripSeconds;

        // A stale datagram lands. It must not become the echoed frame — and since frame 9 was never in
        // the client's ring under a live timestamp, nothing new can be measured from it either.
        session.Client.SendInput(Input(9), session.Now);
        session.Pump(3);

        Assert.Equal(afterNewest, session.Client.RoundTripSeconds, 6);
    }

    // The smoothing is what makes the number readable: one datagram queued behind a burst is not a
    // slower link, and a ping that jumped with every sample would be useless to a person and worse as
    // an input to an adaptive interpolation delay.
    [Fact]
    public void ASpikeMovesTheReadingByAFractionOfItself()
    {
        var session = new Session();
        session.Pump(4);
        session.Client.SendInput(Input(1), session.Now);
        session.Pump();
        double settled = session.Client.RoundTripSeconds;

        // Claim a send a full second in the past: the next echo for that frame measures ~1 s.
        session.Client.SendInput(Input(2), session.Now - 1.0);
        session.Pump();

        Assert.True(session.Client.RoundTripSeconds > settled);
        Assert.True(session.Client.RoundTripSeconds < 0.5,
            $"one spike moved the reading to {session.Client.RoundTripSeconds:0.000} s; it should take "
            + "an eighth of the sample, not the whole of it");
    }

    // A frame whose send time is in the FUTURE relative to the echo is a clock that went backwards, not
    // a negative round trip. It is dropped rather than folded into the average.
    [Fact]
    public void AClockThatWentBackwardsIsNotAMeasurement()
    {
        var session = new Session();
        session.Pump(4);
        session.Client.SendInput(Input(3), session.Now + 60.0);
        session.Pump(2);

        Assert.True(double.IsNaN(session.Client.RoundTripSeconds));
    }

    // Rejoining resets it. Frame numbers start again on the next session, so a stale ring entry could be
    // matched by an echo for a different frame that happens to reuse the number — reporting a round trip
    // as long as the outage.
    [Fact]
    public void ASessionResetForgetsTheReading()
    {
        var session = new Session();
        session.Pump(4);
        session.Client.SendInput(Input(5), session.Now);
        session.Pump();
        Assert.False(double.IsNaN(session.Client.RoundTripSeconds));

        // Past StateTimeout with nothing arriving: the client abandons and rejoins from scratch.
        session.Now += NetClient.StateTimeout + 1.0;
        session.Client.Update(session.Now);

        Assert.False(session.Client.Joined);
        Assert.True(double.IsNaN(session.Client.RoundTripSeconds));
    }

    // A client that has sent nothing is not echoed at all: there is no frame number to report, and
    // echoing a zero would date the reading against a frame that was never sent.
    [Fact]
    public void AClientThatHasSentNoInputIsNotProbed()
    {
        var session = new Session();
        session.Pump(6);

        Assert.Equal(0, session.Server.Traffic.SentDatagramsOf(ENetMessage.InputEcho));

        session.Client.SendInput(Input(1), session.Now);
        session.Pump(2);

        Assert.True(session.Server.Traffic.SentDatagramsOf(ENetMessage.InputEcho) > 0);
    }

    [Fact]
    public void TheEchoRoundTripsThroughItsOwnEncoding()
    {
        byte[] payload = NetMessages.WriteInputEcho(0xDEADBEEF, 4242);

        Assert.Equal(ENetMessage.InputEcho, NetMessages.TypeOf(payload));
        Assert.Equal(9, payload.Length); // the whole probe, against a 4 KB snapshot header it stayed out of
        (uint frame, uint tick) = NetMessages.ReadInputEcho(payload);
        Assert.Equal(0xDEADBEEFu, frame);
        Assert.Equal(4242u, tick);
    }

    private static InputCommand Input(uint frame) =>
        new(frame, 0, 0, jump: false, sprint: false, yaw: 0, pitch: 90);
}
