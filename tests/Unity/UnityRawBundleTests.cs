using System.Collections.Generic;
using System.Text;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class UnityRawBundleTests
{
    [Fact]
    public void IsRaw_DetectsSignature()
    {
        Assert.True(UnityRawBundle.IsRaw(Encoding.ASCII.GetBytes("UnityRaw\0junk")));
        Assert.False(UnityRawBundle.IsRaw(Encoding.ASCII.GetBytes("UnityFS\0")));
        Assert.False(UnityRawBundle.IsRaw(new byte[3])); // too short
    }

    [Fact]
    public void Read_WrongSignature_Throws() =>
        Assert.Throws<System.NotSupportedException>(
            () => UnityRawBundle.Read(Encoding.ASCII.GetBytes("UnityFS\0")));

    [Fact]
    public void Read_ExtractsEntryPayload()
    {
        byte[] payload = { 10, 20, 30, 40, 50 };
        byte[] bundle = RawBundleBytes.Wrap("CAB-test", payload);

        UnityRawBundle raw = UnityRawBundle.Read(bundle);

        KeyValuePair<string, byte[]> file = Assert.Single(raw.Files);
        Assert.Equal("CAB-test", file.Key);
        Assert.Equal(payload, file.Value);
    }

}
