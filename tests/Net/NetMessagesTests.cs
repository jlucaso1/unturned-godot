using System.Collections.Generic;
using Godot;
using UnturnedGodot.Net;
using Xunit;

namespace UnturnedGodot.Tests.Net;

public class NetMessagesTests
{
    [Theory]
    [InlineData(0f, 0)]
    [InlineData(90f, 45)]
    [InlineData(359f, 180)] // 359/2 = 179.5 -> rounds to 180
    [InlineData(-90f, 135)] // wraps to 270 -> 135
    [InlineData(720.5f, 0)] // wraps to 0.5 -> byte 0 (0.25 rounds down)
    public void YawQuantization_HalvedDegreesInAByte(float degrees, byte expected)
    {
        Assert.Equal(expected, NetAngles.QuantizeYaw(degrees));
    }

    [Fact]
    public void YawRoundTrip_Within2Degrees()
    {
        for (float yaw = 0f; yaw < 360f; yaw += 7.3f)
        {
            float back = NetAngles.DequantizeYaw(NetAngles.QuantizeYaw(yaw));
            float error = Mathf.Abs(Mathf.Wrap(back - yaw, -180f, 180f));
            Assert.True(error <= 1.0f, $"yaw {yaw} -> {back} (error {error})");
        }
    }

    [Fact]
    public void PitchQuantization_ClampsAndRounds()
    {
        Assert.Equal(0, NetAngles.QuantizePitch(-10f));
        Assert.Equal(90, NetAngles.QuantizePitch(89.7f));
        Assert.Equal(180, NetAngles.QuantizePitch(500f));
        Assert.Equal(45f, NetAngles.DequantizePitch(45));
    }

    [Fact]
    public void Hello_RoundTrips_WithProtocolVersionAndLevel()
    {
        byte[] p = NetMessages.WriteHello("Joao", "California 2");
        Assert.Equal(ENetMessage.Hello, NetMessages.TypeOf(p));
        (byte version, string name, string level) = NetMessages.ReadHello(p);
        Assert.Equal(NetMessages.ProtocolVersion, version);
        Assert.Equal("Joao", name);
        Assert.Equal("California 2", level);
    }

    [Fact]
    public void ServerInfo_RoundTrips()
    {
        byte[] p = NetMessages.WriteServerInfo("PEI", playerCount: 4, freeSlots: 250);
        Assert.Equal(ENetMessage.ServerInfo, NetMessages.TypeOf(p));
        ServerInfo info = NetMessages.ReadServerInfo(p);
        Assert.Equal(NetMessages.ProtocolVersion, info.ProtocolVersion);
        Assert.Equal("PEI", info.Level);
        Assert.Equal(4, info.PlayerCount);
        Assert.Equal(250, info.FreeSlots);
    }

    // The counts are bytes on the wire; a server that somehow reports more saturates instead of
    // wrapping a 300-player count round to 44.
    [Fact]
    public void ServerInfo_SaturatesOutOfRangeCounts()
    {
        ServerInfo info = NetMessages.ReadServerInfo(
            NetMessages.WriteServerInfo("PEI", playerCount: 300, freeSlots: -1));
        Assert.Equal(255, info.PlayerCount);
        Assert.Equal(0, info.FreeSlots);
    }

    [Fact]
    public void ServerInfoRequest_IsASingleTypeByte()
    {
        byte[] p = NetMessages.WriteServerInfoRequest();
        Assert.Equal(ENetMessage.ServerInfoRequest, NetMessages.TypeOf(p));
        Assert.Single(p);
    }

    [Fact]
    public void Reject_RoundTrips()
    {
        byte[] p = NetMessages.WriteReject(EJoinRejection.LevelMismatch, "PEI");
        Assert.Equal(ENetMessage.Reject, NetMessages.TypeOf(p));
        JoinRejection rejection = NetMessages.ReadReject(p);
        Assert.Equal(EJoinRejection.LevelMismatch, rejection.Reason);
        Assert.Equal(NetMessages.ProtocolVersion, rejection.ServerProtocolVersion);
        Assert.Equal("PEI", rejection.ServerLevel);
    }

    // Map names reach the wire from menus, folder listings and command lines. Only a real difference
    // is a different world — and nothing at all is never a match, since a client that cannot name its
    // world is the one this check exists to catch.
    [Theory]
    [InlineData("PEI", "PEI", true)]
    [InlineData("pei", "PEI", true)]
    [InlineData("  PEI\t", "PEI", true)]
    [InlineData("California 2", "PEI", false)]
    [InlineData("", "PEI", false)]
    [InlineData("PEI", "", false)]
    [InlineData("", "", false)]
    [InlineData("   ", "PEI", false)]
    public void LevelsMatch_IsCaseAndWhitespaceInsensitive_ButNeverEmpty(string a, string b, bool expected) =>
        Assert.Equal(expected, NetMessages.LevelsMatch(a, b));

    [Fact]
    public void Welcome_RoundTrips()
    {
        var players = new List<PlayerListing>
        {
            new() { PlayerId = 2, Name = "Ana", Position = new Vector3(1, 2, 3), Pitch = 90, Yaw = 45,
                Stance = UnturnedGodot.Player.EPlayerStance.Prone },
            new() { PlayerId = 7, Name = "Bo", Position = new Vector3(-4, 5, -6), Pitch = 10, Yaw = 170 },
        };
        byte[] p = NetMessages.WriteWelcome(9, 1234, players);

        (byte id, uint tick, List<PlayerListing> read) = NetMessages.ReadWelcome(p);
        Assert.Equal(9, id);
        Assert.Equal(1234u, tick);
        Assert.Equal(2, read.Count);
        Assert.Equal("Ana", read[0].Name);
        Assert.Equal(UnturnedGodot.Player.EPlayerStance.Prone, read[0].Stance);
        Assert.Equal(new Vector3(-4, 5, -6), read[1].Position);
        Assert.Equal(170, read[1].Yaw);
    }

    [Fact]
    public void JoinedAndLeft_RoundTrip()
    {
        var listing = new PlayerListing { PlayerId = 3, Name = "Cy", Position = Vector3.One, Pitch = 1, Yaw = 2 };
        (uint tick, PlayerListing joined) =
            NetMessages.ReadPlayerJoined(NetMessages.WritePlayerJoined(4321, listing));
        Assert.Equal(4321u, tick); // when they joined, so a roster older than that cannot bury them
        Assert.Equal(3, joined.PlayerId);
        Assert.Equal("Cy", joined.Name);

        Assert.Equal(5, NetMessages.ReadPlayerLeft(NetMessages.WritePlayerLeft(5)));
    }

    [Fact]
    public void Input_RoundTrips_AllFlagCombinations()
    {
        foreach ((bool jump, bool sprint) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            var input = new InputCommand(42, -1, 1, jump, sprint, yaw: 100, pitch: 45,
                UnturnedGodot.Player.EPlayerStance.Crouch);
            InputCommand read = NetMessages.ReadInput(NetMessages.WriteInput(input));
            Assert.Equal(42u, read.Frame);
            Assert.Equal(-1, read.InputX);
            Assert.Equal(1, read.InputY);
            Assert.Equal(jump, read.Jump);
            Assert.Equal(sprint, read.Sprint);
            Assert.Equal(100, read.Yaw);
            Assert.Equal(45, read.Pitch);
            Assert.Equal(UnturnedGodot.Player.EPlayerStance.Crouch, read.Stance);
            Assert.False(read.HasPosition);
        }
    }

    [Fact]
    public void Input_RoundTrips_TrustedPosition()
    {
        var input = new InputCommand(7, 0, -1, false, true, 10, 90,
            UnturnedGodot.Player.EPlayerStance.Prone, new Vector3(300.5f, 34.25f, -84f), grounded: false);
        InputCommand read = NetMessages.ReadInput(NetMessages.WriteInput(input));
        Assert.True(read.HasPosition);
        Assert.Equal(new Vector3(300.5f, 34.25f, -84f), read.Position);
        Assert.Equal(UnturnedGodot.Player.EPlayerStance.Prone, read.Stance);
        Assert.False(read.Grounded);
    }

    [Fact]
    public void StateUpdate_RoundTrips()
    {
        var states = new List<PlayerSnapshotState>
        {
            new(1, new Vector3(10, 20, 30), 90, 0, UnturnedGodot.Player.EPlayerStance.Crouch,
                moving: true, grounded: false),
            new(2, new Vector3(-1.5f, 0.25f, 7f), 45, 128),
        };
        byte[] p = NetMessages.WriteStateUpdate(77, states);

        (uint tick, List<PlayerSnapshotState> read) = NetMessages.ReadStateUpdate(p);
        Assert.Equal(77u, tick);
        Assert.Equal(2, read.Count);
        Assert.Equal(1, read[0].PlayerId);
        Assert.Equal(new Vector3(-1.5f, 0.25f, 7f), read[1].Position);
        Assert.Equal(128, read[1].Yaw);
        Assert.Equal(UnturnedGodot.Player.EPlayerStance.Crouch, read[0].Stance);
        Assert.True(read[0].Moving);   // moving and grounded share the stance byte without corrupting it
        Assert.False(read[0].Grounded); // airborne mid-jump
        Assert.Equal(UnturnedGodot.Player.EPlayerStance.Stand, read[1].Stance);
        Assert.False(read[1].Moving);
        Assert.True(read[1].Grounded);
    }

    // Byte-for-byte wire layout — what a little-endian BinaryWriter historically produced — so
    // serializer rewrites provably ship the same bytes.
    [Fact]
    public void StateUpdate_WireLayout_IsExact()
    {
        var states = new List<PlayerSnapshotState>
        {
            new(3, new Vector3(1f, -2f, 0.5f), 9, 7, UnturnedGodot.Player.EPlayerStance.Stand,
                moving: false, grounded: true),
        };
        byte[] payload = NetMessages.WriteStateUpdate(0x04030201u, states);

        var expected = new List<byte> { (byte)ENetMessage.StateUpdate, 0x01, 0x02, 0x03, 0x04, 1, 3 };
        expected.AddRange(System.BitConverter.GetBytes(1f));
        expected.AddRange(System.BitConverter.GetBytes(-2f));
        expected.AddRange(System.BitConverter.GetBytes(0.5f));
        expected.Add(9);
        expected.Add(7);
        expected.Add(0x43); // stance Stand (3) | grounded (0x40); moving (0x80) unset
        Assert.Equal(expected.ToArray(), payload);
    }
}
