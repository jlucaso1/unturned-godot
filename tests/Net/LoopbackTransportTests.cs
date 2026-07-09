using UnturnedGodot.Net;
using Xunit;

namespace UnturnedGodot.Tests.Net;

public class LoopbackTransportTests
{
    [Fact]
    public void ConnectAndExchange_BothDirections()
    {
        var server = new LoopbackServerTransport();
        LoopbackClientTransport client = server.CreateClient();

        Assert.True(server.TryReceive(out ServerTransportEvent connected));
        Assert.Equal(ETransportEvent.Connected, connected.Type);

        client.Send(new byte[] { 1, 2 }, ESendType.Reliable);
        Assert.True(server.TryReceive(out ServerTransportEvent msg));
        Assert.Equal(ETransportEvent.Message, msg.Type);
        Assert.Equal(new byte[] { 1, 2 }, msg.Payload);
        Assert.Equal(connected.Connection, msg.Connection);

        msg.Connection.Send(new byte[] { 9 }, ESendType.Unreliable);
        Assert.True(client.TryReceive(out byte[] reply));
        Assert.Equal(new byte[] { 9 }, reply);
        Assert.False(client.TryReceive(out _));

        client.Update(0); // no-ops for coverage of the polled contract
        server.Update(0);
    }

    [Fact]
    public void ClientClose_ReportsDisconnect_AndStopsSending()
    {
        var server = new LoopbackServerTransport();
        LoopbackClientTransport client = server.CreateClient();
        server.TryReceive(out _); // connected

        client.Close();
        client.Close(); // idempotent
        Assert.False(client.IsConnected);
        client.Send(new byte[] { 1 }, ESendType.Reliable); // dropped, not queued

        Assert.True(server.TryReceive(out ServerTransportEvent evt));
        Assert.Equal(ETransportEvent.Disconnected, evt.Type);
        Assert.False(server.TryReceive(out _));
    }

    [Fact]
    public void ServerKick_ClosesTheClientSide()
    {
        var server = new LoopbackServerTransport();
        LoopbackClientTransport client = server.CreateClient();
        server.TryReceive(out ServerTransportEvent connected);

        connected.Connection.Close();
        Assert.False(client.IsConnected);
        Assert.True(server.TryReceive(out ServerTransportEvent evt));
        Assert.Equal(ETransportEvent.Disconnected, evt.Type);
        Assert.True(connected.Connection.Id > 0);
    }

    [Fact]
    public void ServerClose_ClearsPendingEvents()
    {
        var server = new LoopbackServerTransport();
        server.CreateClient();
        server.Close();
        Assert.False(server.TryReceive(out _));
    }
}
