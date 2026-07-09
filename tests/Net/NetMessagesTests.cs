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
    public void Hello_RoundTrips_WithProtocolVersion()
    {
        byte[] p = NetMessages.WriteHello("Joao");
        Assert.Equal(ENetMessage.Hello, NetMessages.TypeOf(p));
        (byte version, string name) = NetMessages.ReadHello(p);
        Assert.Equal(NetMessages.ProtocolVersion, version);
        Assert.Equal("Joao", name);
    }

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
        PlayerListing joined = NetMessages.ReadPlayerJoined(NetMessages.WritePlayerJoined(listing));
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
            UnturnedGodot.Player.EPlayerStance.Prone, new Vector3(300.5f, 34.25f, -84f));
        InputCommand read = NetMessages.ReadInput(NetMessages.WriteInput(input));
        Assert.True(read.HasPosition);
        Assert.Equal(new Vector3(300.5f, 34.25f, -84f), read.Position);
        Assert.Equal(UnturnedGodot.Player.EPlayerStance.Prone, read.Stance);
    }

    [Fact]
    public void StateUpdate_RoundTrips()
    {
        var states = new List<PlayerSnapshotState>
        {
            new(1, new Vector3(10, 20, 30), 90, 0, UnturnedGodot.Player.EPlayerStance.Crouch, moving: true),
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
        Assert.True(read[0].Moving);  // the moving bit shares the stance byte and must not corrupt it
        Assert.Equal(UnturnedGodot.Player.EPlayerStance.Stand, read[1].Stance);
        Assert.False(read[1].Moving);
    }
}
