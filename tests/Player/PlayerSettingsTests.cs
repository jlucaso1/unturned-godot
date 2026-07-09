using Godot;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Player;

public class PlayerSettingsTests
{
    // A fresh install must match Unturned's own ControlsSettings/OptionsSettings defaults.
    [Fact]
    public void Defaults_MatchUnturned()
    {
        PlayerSettings s = PlayerSettings.Default;
        Assert.Equal(0.2f, s.MouseSensitivity);
        Assert.False(s.InvertLook);
        Assert.Equal(90f, s.VerticalFovDegrees);
        Assert.Equal(10f, s.SprintFovBoost);
        Assert.Equal(EControlMode.Toggle, s.CrouchMode);
        Assert.Equal(EControlMode.Toggle, s.ProneMode);
        Assert.Equal(EControlMode.Hold, s.SprintMode);
        Assert.Equal(Key.W, s.Forward);
        Assert.Equal(Key.S, s.Back);
        Assert.Equal(Key.A, s.Left);
        Assert.Equal(Key.D, s.Right);
        Assert.Equal(Key.Space, s.Jump);
        Assert.Equal(Key.X, s.Crouch);
        Assert.Equal(Key.Z, s.Prone);
        Assert.Equal(Key.Shift, s.Sprint);
        Assert.Equal(Key.H, s.Perspective);
    }
}
