using Vemo.Core;
using Vemo.Diarization;
using Xunit;
namespace Vemo.Core.Tests;
public sealed class SpeakerTurnBoundsTests
{
    [Fact] public void ObservedQuantizedEndpointClampsToExactDecodedDuration()
    {
        var result = SpeakerTurnBounds.Normalize(new(0, 43.787845611572266, "a"), 43.77175);
        Assert.Equal(43.77175, result.EndSeconds); Assert.Equal(0, result.StartSeconds);
        Assert.Equal("a", result.SpeakerId);
    }
    [Theory]
    [InlineData(0, 1)] [InlineData(0.5, 1)] [InlineData(0, 3600)]
    public void EndpointWithinAudioRemainsExact(double start, double end)
    { var turn = new SpeakerTurn(start, end, "a"); Assert.Equal(turn, SpeakerTurnBounds.Normalize(turn, end)); }
    [Fact] public void BoundedToleranceWorksAtOneHourAndRejectsBeyondIt()
    {
        Assert.Equal(3600, SpeakerTurnBounds.Normalize(new(3599, 3600 + SpeakerTurnBounds.FrameEndTolerance, "a"), 3600).EndSeconds);
        Assert.Throws<InvalidDataException>(() => SpeakerTurnBounds.Normalize(new(3599, 3600 + SpeakerTurnBounds.FrameEndTolerance + 0.000001, "a"), 3600));
    }
    [Theory]
    [InlineData(-0.001, 1, 1)] [InlineData(1, 0.5, 1)] [InlineData(1, 1, 1)]
    [InlineData(0, 1.1, 1)] [InlineData(0, 1, 3601)] [InlineData(0, 0, 0)]
    [InlineData(double.NaN, 1, 1)] [InlineData(0, double.PositiveInfinity, 1)]
    public void InvalidTurnIsRejected(double start, double end, double duration)
        => Assert.Throws<InvalidDataException>(() => SpeakerTurnBounds.Normalize(new(start, end, "a"), duration));
}
