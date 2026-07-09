using System.IO;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class AudioDefCacheTests
{
    [Fact]
    public void RoundTrip()
    {
        var def = new OneShotAudioDef(0.6f, 0.95f, 1.05f,
            new[] { "footstep_grass_walk_01.ogg", "footstep_grass_walk_02.ogg" });
        using var ms = new MemoryStream();
        AudioDefCache.Write(ms, def);
        ms.Position = 0;
        OneShotAudioDef read = AudioDefCache.Read(ms);

        Assert.Equal(0.6f, read.VolumeMultiplier);
        Assert.Equal(0.95f, read.MinPitch);
        Assert.Equal(1.05f, read.MaxPitch);
        Assert.Equal(def.ClipFiles, read.ClipFiles);
    }

    [Fact]
    public void Read_BadMagic_Throws()
    {
        using var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.Throws<InvalidDataException>(() => AudioDefCache.Read(ms));
    }
}
