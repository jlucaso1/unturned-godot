using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnturnedGodot.Net;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests.Net;

// A UdpServerTransport connection is keyed by address AND port, and every new key allocates a Connection
// plus a ReliableChannel. A sender that varies its source port grew that table without limit, from one
// host, before saying anything the server would recognise — no handshake, nothing to authenticate.
//
// Traited because this is one of the two files here that can flake: it binds real ports (through a
// FreePort probe with a TOCTOU window) and sleeps on localhost datagram scheduling. See TestCategories.
[Trait(TestCategories.Name, TestCategories.RealSockets)]
public class ConnectionCapTests
{
    private static ushort FreePort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return (ushort)((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    // Every socket here shares 127.0.0.1, so this exercises the per-address cap: one host cannot fill the
    // table however many source ports it spends.
    [Fact]
    public void OneHostCannotOpenMoreConnectionsThanItsPerAddressCap()
    {
        ushort port = FreePort();
        var transport = new UdpServerTransport(port);
        var server = new IPEndPoint(IPAddress.Loopback, port);

        var sockets = new List<UdpClient>();
        try
        {
            const int attempts = UdpServerTransport.MaxConnectionsPerAddress * 4;
            for (int i = 0; i < attempts; i++)
            {
                var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                sockets.Add(client);
                // A bare unreliable prefix: enough to reach the connection table, nothing more.
                client.Send(new[] { ReliableChannel.ChannelUnreliable, (byte)ENetMessage.Hello }, 2, server);
            }

            // Give the datagrams a moment to land, then drain.
            System.Threading.Thread.Sleep(200);
            int connected = 0;
            transport.Update(0.0);
            while (transport.TryReceive(out ServerTransportEvent evt))
                if (evt.Type == ETransportEvent.Connected)
                    connected++;

            Assert.True(connected <= UdpServerTransport.MaxConnectionsPerAddress,
                $"{connected} connections from one address, cap is " +
                $"{UdpServerTransport.MaxConnectionsPerAddress}");
            Assert.True(transport.RefusedConnections > 0, "nothing was refused");
        }
        finally
        {
            foreach (UdpClient client in sockets)
                client.Dispose();
            transport.Close();
        }
    }

    // The cap must not cost a legitimate client its connection: well under the limit, everyone gets in.
    [Fact]
    public void ClientsUnderTheCapAllConnect()
    {
        ushort port = FreePort();
        var transport = new UdpServerTransport(port);
        var server = new IPEndPoint(IPAddress.Loopback, port);

        var sockets = new List<UdpClient>();
        try
        {
            const int count = 3;
            for (int i = 0; i < count; i++)
            {
                var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                sockets.Add(client);
                client.Send(new[] { ReliableChannel.ChannelUnreliable, (byte)ENetMessage.Hello }, 2, server);
            }

            System.Threading.Thread.Sleep(200);
            int connected = 0;
            transport.Update(0.0);
            while (transport.TryReceive(out ServerTransportEvent evt))
                if (evt.Type == ETransportEvent.Connected)
                    connected++;

            Assert.Equal(count, connected);
            Assert.Equal(0, transport.RefusedConnections);
        }
        finally
        {
            foreach (UdpClient client in sockets)
                client.Dispose();
            transport.Close();
        }
    }
}
