using Vemo.Core;
namespace Vemo.Diarization;

public static class SpeakerTurnBounds
{
    // Pinned pyannote receptive field 991 / 16 kHz, half-frame endpoint,
    // plus two float32 ULPs at the supported one-hour maximum. Matches Mac.
    public const double FrameEndTolerance = 991d / 32000 + 2d / 4096;
    public static SpeakerTurn Normalize(SpeakerTurn turn, double duration)
    {
        var start = turn.StartSeconds; var end = turn.EndSeconds;
        if (!double.IsFinite(start) || !double.IsFinite(end) || !double.IsFinite(duration) || duration <= 0 || duration > 3600 || start < 0 || end < start || start >= duration || end > duration + FrameEndTolerance)
            throw new InvalidDataException("Speaker turn lies outside decoded audio duration.");
        return turn with { EndSeconds = Math.Min(end, duration) };
    }
}
