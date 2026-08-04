using System.Collections.Generic;
using Godot;
using UnturnedGodot.Repro;
using Xunit;

namespace UnturnedGodot.Tests.Repro;

// The recording, served back. What matters here is that it answers the questions it recorded, in the
// order it recorded them, and refuses everything else instead of guessing — a plausible answer to a
// question the session never asked is how a replay ends up "reproducing" a bug that is no longer there.
public class ReproOracleTests
{
    private static ReproOracleData Data()
    {
        var data = new ReproOracleData();
        // Two move calls in tick 0 with different arguments, one in tick 1.
        Move(data, 0, new Vector3(1f, 0f, 0f), new Vector3(2f, 0f, 0f), 0.4f, new Vector3(1.5f, 0f, 0f));
        Move(data, 0, new Vector3(3f, 0f, 0f), new Vector3(4f, 0f, 0f), 0.4f, new Vector3(3.5f, 0f, 0f));
        Move(data, 1, new Vector3(9f, 0f, 0f), new Vector3(9.5f, 0f, 0f), 0.75f, new Vector3(9.1f, 0f, 0f));

        data.Ground.AddRange(new[] { 0f, 7f, 1f, 0f, 0f, 1f, 12.5f });
        data.Vision.AddRange(new[] { 0f, 7f, 0f, 1f, 0f, 5f, 1f, 0f, 1f });

        data.Path.AddRange(new[] { 1f, 7f, 0f, 0f, 0f, 10f, 0f, 10f, 0.4f, 1f, 0f, 2f });
        data.PathPoints.AddRange(new[] { 0f, 0f, 0f, 10f, 0f, 10f });

        data.Ready.AddRange(new[] { 0f, 1f, 1f, 0f });
        return data;
    }

    private static void Move(ReproOracleData data, int tick, Vector3 from, Vector3 to, float radius,
        Vector3 result)
    {
        data.Move.AddRange(new[]
        {
            tick, 0f, from.X, from.Y, from.Z, to.X, to.Y, to.Z, radius, result.X, result.Y, result.Z,
        });
    }

    [Fact]
    public void ItAnswersWhatItRecorded()
    {
        var oracle = new ReproOracle(Data(), 2);
        oracle.SeekTick(0);
        Assert.True(oracle.TryMove(new Vector3(1f, 0f, 0f), new Vector3(2f, 0f, 0f), 0.4f,
            out Vector3 result));
        Assert.Equal(new Vector3(1.5f, 0f, 0f), result);
        Assert.Equal(1, oracle.Hits);
        Assert.Equal(0, oracle.Misses);
    }

    // Order matters: the resolver is called twice per body per tick and the second answer is not the
    // first one.
    [Fact]
    public void RepeatedCallsAreServedInOrder()
    {
        var oracle = new ReproOracle(Data(), 2);
        oracle.SeekTick(0);
        oracle.TryMove(new Vector3(1f, 0f, 0f), new Vector3(2f, 0f, 0f), 0.4f, out _);
        Assert.True(oracle.TryMove(new Vector3(3f, 0f, 0f), new Vector3(4f, 0f, 0f), 0.4f,
            out Vector3 second));
        Assert.Equal(new Vector3(3.5f, 0f, 0f), second);

        // The first entry is spent; asking again in the same tick is a miss, not a repeat.
        Assert.False(oracle.TryMove(new Vector3(1f, 0f, 0f), new Vector3(2f, 0f, 0f), 0.4f, out _));
    }

    // Out of order, but the same question: still answered, by wrapping back through the tick.
    [Fact]
    public void AReorderedQueryStillMatches()
    {
        var oracle = new ReproOracle(Data(), 2);
        oracle.SeekTick(0);
        Assert.True(oracle.TryMove(new Vector3(3f, 0f, 0f), new Vector3(4f, 0f, 0f), 0.4f, out _));
        Assert.True(oracle.TryMove(new Vector3(1f, 0f, 0f), new Vector3(2f, 0f, 0f), 0.4f, out _));
    }

    [Fact]
    public void ADifferentQuestionIsAMiss()
    {
        var oracle = new ReproOracle(Data(), 2);
        oracle.SeekTick(0);
        Assert.False(oracle.TryMove(new Vector3(1f, 0f, 0f), new Vector3(2f, 5f, 0f), 0.4f, out _));
        Assert.False(oracle.TryMove(new Vector3(1f, 0f, 0f), new Vector3(2f, 0f, 0f), 0.75f, out _));
        Assert.Equal(2, oracle.Misses);
    }

    [Fact]
    public void AnswersAreScopedToTheirTick()
    {
        var oracle = new ReproOracle(Data(), 2);
        oracle.SeekTick(1);
        Assert.False(oracle.TryMove(new Vector3(1f, 0f, 0f), new Vector3(2f, 0f, 0f), 0.4f, out _));
        Assert.True(oracle.TryMove(new Vector3(9f, 0f, 0f), new Vector3(9.5f, 0f, 0f), 0.75f, out _));

        // Past the recorded window there is nothing at all.
        oracle.SeekTick(9);
        Assert.False(oracle.TryMove(new Vector3(9f, 0f, 0f), new Vector3(9.5f, 0f, 0f), 0.75f, out _));
    }

    [Fact]
    public void SeekingATickAgainRewindsIt()
    {
        var oracle = new ReproOracle(Data(), 2);
        oracle.SeekTick(0);
        Assert.True(oracle.TryMove(new Vector3(1f, 0f, 0f), new Vector3(2f, 0f, 0f), 0.4f, out _));
        oracle.SeekTick(0);
        Assert.True(oracle.TryMove(new Vector3(1f, 0f, 0f), new Vector3(2f, 0f, 0f), 0.4f, out _));
    }

    [Fact]
    public void GroundVisionAndPathsComeBackToo()
    {
        var oracle = new ReproOracle(Data(), 2);
        oracle.SeekTick(0);
        Assert.True(oracle.TryGround(new Vector3(1f, 0f, 0f), out bool hit, out float y));
        Assert.True(hit);
        Assert.Equal(12.5f, y);
        Assert.False(oracle.TryGround(new Vector3(50f, 0f, 0f), out _, out _));

        Assert.True(oracle.TryVision(new Vector3(0f, 1f, 0f), new Vector3(5f, 1f, 0f),
            out bool blocked));
        Assert.True(blocked);

        var path = new List<Vector3>();
        oracle.SeekTick(1);
        Assert.True(oracle.TryPath(Vector3.Zero, new Vector3(10f, 0f, 10f), 0.4f, path,
            out bool found));
        Assert.True(found);
        Assert.Equal(2, path.Count);
        Assert.Equal(new Vector3(10f, 0f, 10f), path[1]);
    }

    [Fact]
    public void ReadinessIsPerTickAndDefaultsToReady()
    {
        var oracle = new ReproOracle(Data(), 2);
        oracle.SeekTick(0);
        Assert.True(oracle.Ready());
        oracle.SeekTick(1);
        Assert.False(oracle.Ready());
        oracle.SeekTick(5); // nothing recorded: the system's own default
        Assert.True(oracle.Ready());
    }

    [Fact]
    public void AnEmptyRecordingAnswersNothing()
    {
        var oracle = new ReproOracle(new ReproOracleData(), 0);
        oracle.SeekTick(0);
        Assert.False(oracle.TryMove(Vector3.Zero, Vector3.One, 0.4f, out _));
        Assert.False(oracle.TryGround(Vector3.Zero, out _, out _));
        Assert.False(oracle.TryVision(Vector3.Zero, Vector3.One, out _));
        Assert.False(oracle.TryPath(Vector3.Zero, Vector3.One, 0.4f, new List<Vector3>(), out _));
        Assert.Equal(4, oracle.Misses);
    }

    // A route that points past the end of the stored points (a truncated file) yields what is there
    // rather than throwing in the middle of a replay.
    [Fact]
    public void ATruncatedRouteIsClamped()
    {
        var data = new ReproOracleData();
        data.Path.AddRange(new[] { 0f, 0f, 0f, 0f, 0f, 1f, 0f, 1f, 0.4f, 1f, 0f, 4f });
        data.PathPoints.AddRange(new[] { 0f, 0f, 0f });
        var oracle = new ReproOracle(data, 1);
        oracle.SeekTick(0);
        var path = new List<Vector3>();
        Assert.True(oracle.TryPath(Vector3.Zero, new Vector3(1f, 0f, 1f), 0.4f, path, out _));
        Assert.Single(path);
    }

    [Fact]
    public void NullArgumentsAreRefused()
    {
        Assert.Throws<System.ArgumentNullException>(() => new ReproOracle(null!, 1));
        Assert.Throws<System.ArgumentNullException>(() =>
            new ReproOracle(new ReproOracleData(), 1).TryPath(Vector3.Zero, Vector3.One, 0.4f,
                null!, out _));
    }
}
