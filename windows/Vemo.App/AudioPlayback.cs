using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Vemo.App;

/// <summary>Owns one local decoder and opens an output device only on explicit Play.</summary>
public sealed class AudioPlayback : IDisposable
{
    MediaFoundationReader? reader;
    WasapiOut? output;
    TimeSpan offset;
    TimeSpan duration;
    bool disposed;
    volatile string? playbackError;
    volatile bool ended;
    public bool IsSupported => OperatingSystem.IsWindows();
    public bool HasSource => reader != null;
    public bool IsPlaying => output?.PlaybackState == PlaybackState.Playing;
    public string? Error { get; private set; }
    public TimeSpan Duration => duration;
    public TimeSpan Position
    {
        get
        {
            try
            {
                return output == null ? reader?.CurrentTime ?? TimeSpan.Zero
                    : TimeSpan.FromSeconds(Math.Clamp(offset.TotalSeconds + (double)output.GetPosition() / output.OutputWaveFormat.AverageBytesPerSecond, 0, Duration.TotalSeconds));
            }
            catch (Exception e) { Release(); Error = "오디오 위치 확인 실패: " + e.Message; return TimeSpan.Zero; }
        }
    }

    public void SetSource(string? path)
    {
        Release(); Error = null;
        if (disposed || path == null) return;
        if (!IsSupported) { Error = "오디오 재생은 Windows에서 사용할 수 있습니다."; return; }
        Try(() =>
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
                throw new IOException("로컬 오디오 파일을 찾을 수 없습니다.");
            reader = new MediaFoundationReader(path);
            duration = reader.TotalTime;
        });
    }

    public void Play()
    {
        if (disposed || reader == null) return;
        Try(() =>
        {
            if (Position >= Duration) SeekCore(TimeSpan.Zero);
            if (output == null)
            {
                offset = reader.CurrentTime;
                var device = new WasapiOut(AudioClientShareMode.Shared, false, 100);
                output = device;
                device.PlaybackStopped += (_, e) =>
                {
                    if (!ReferenceEquals(output, device)) return;
                    if (e.Exception != null) playbackError = e.Exception.Message;
                    else ended = true;
                };
                device.Init(reader);
            }
            output!.Play();
        });
    }

    public void Pause() => Try(() => output?.Pause());
    public void Seek(TimeSpan position)
    {
        if (disposed || reader == null) return;
        Try(() =>
        {
            var resume = IsPlaying;
            SeekCore(position);
            if (resume) Play();
        });
    }
    void SeekCore(TimeSpan position)
    {
        ReleaseOutput();
        if (reader != null) reader.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, Duration.TotalSeconds));
    }
    public void Stop()
    {
        if (!disposed) Try(() => SeekCore(TimeSpan.Zero));
    }
    // Called by the UI timer, including when a device failure arrives on an audio thread.
    public void Refresh()
    {
        if (playbackError is { } message) { Release(); Error = message; }
        else if (ended) Try(() => SeekCore(Duration));
    }
    void Try(Action action)
    {
        try { action(); }
        catch (Exception e) { Release(); Error = "오디오 재생 실패: " + e.Message; }
    }
    void ReleaseOutput()
    {
        var old = output; output = null;
        try { old?.Stop(); }
        finally { old?.Dispose(); }
        playbackError = null;
        ended = false;
    }
    void Release()
    {
        try { ReleaseOutput(); }
        catch (Exception e) { Error = "오디오 종료 실패: " + e.Message; }
        finally
        {
            var old = reader; reader = null;
            duration = TimeSpan.Zero;
            try { old?.Dispose(); }
            catch (Exception e) { Error = "오디오 종료 실패: " + e.Message; }
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Release();
    }
}
