using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Net;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Net;

// The full stack over REAL localhost UDP sockets: server + two clients, joins, movement, visibility.
// Same scenario the loopback E2E proves, now through the wire (framing, acks, endpoint tracking).
public class UdpEndToEndTests
{
    private static bool FlatGround(float x, float z, out float y)
    {
        y = 0f;
        return true;
    }

    private static ushort FreePort()
    {
        // Bind port 0 to let the OS pick, then reuse that port for the test server.
        using var probe = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        return (ushort)((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    [Fact]
    public void TwoClientsOverRealSockets_SeeEachOtherMove()
    {
        ushort port = FreePort();
        var serverTransport = new UdpServerTransport(port);
        var server = new NetServer(serverTransport,
            new ServerSimulation(new HeightfieldMoveSolver(FlatGround)), new Vector3(0, 0, 0));

        var aTransport = new UdpClientTransport("127.0.0.1", port);
        var bTransport = new UdpClientTransport("127.0.0.1", port);
        var a = new NetClient(aTransport, "A");
        var b = new NetClient(bTransport, "B");

        try
        {
            double now = 5000.0; // absolute clock: regression for the constructor-time Hello give-up bug
            void Pump(int rounds)
            {
                for (int i = 0; i < rounds; i++)
                {
                    now += ServerSimulation.TickRate;
                    server.Update(now);
                    a.Update(now);
                    b.Update(now);
                    System.Threading.Thread.Sleep(2); // let localhost datagrams land
                }
            }

            Pump(10); // handshakes settle (reliable Hello/Welcome/Joined over real sockets)
            Assert.True(a.Joined && b.Joined, $"joined: A={a.Joined} B={b.Joined}");
            Assert.Single(a.Remotes);
            Assert.Single(b.Remotes);

            for (int i = 0; i < 25; i++)
            {
                a.SendInput(new InputCommand((uint)i, 0, -1, false, false, 0, 90));
                Pump(1);
            }
            Pump(5);

            PoseSnapshot seen = b.Remotes[a.PlayerId].Sample(now);
            Assert.True(seen.Position.Z < -4f, $"B sees A at Z={seen.Position.Z}");
        }
        finally
        {
            aTransport.Close();
            bTransport.Close();
            serverTransport.Close();
        }
    }
}
