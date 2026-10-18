using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NAudio.Wave;
using Vemo.App;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Vemo.Playback.Tests.TestAppBuilder))]
namespace Vemo.Playback.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class PlaybackTests
{
    [Fact]
    public void Unsupported_or_invalid_source_is_safe_and_releases_file()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        File.WriteAllBytes(path, "RIFF!"u8.ToArray());
        try
        {
            using var audio = new AudioPlayback();
            audio.SetSource(path);
            Assert.False(audio.HasSource); Assert.False(audio.IsPlaying); Assert.NotNull(audio.Error);
            Assert.Equal(TimeSpan.Zero, audio.Position);
            audio.Play(); audio.Pause(); audio.Seek(TimeSpan.MaxValue); audio.Stop();
            audio.SetSource(null); Assert.Null(audio.Error);
            using var unlocked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public void View_displays_error_caches_failed_source_and_disposes_safely()
    {
        using var player = new AudioPlayerView();
        var window = new Window { Content = player }; window.Show();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        File.WriteAllBytes(path, "RIFF!"u8.ToArray());
        try
        {
            player.SetSource(path);
            Assert.All(player.GetVisualDescendants().OfType<Slider>(), s => Assert.False(s.IsEnabled));
            Assert.Contains(player.GetVisualDescendants().OfType<TextBlock>(), t => t.IsVisible && t.Text?.Contains(OperatingSystem.IsWindows() ? "실패" : "Windows") == true);
            var backend = (AudioPlayback)typeof(AudioPlayerView).GetField("playback", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player)!;
            backend.SetSource(null); // Detect any attempted load on the same failed path.
            player.SetSource(path); Assert.Null(backend.Error);
            player.SetSource(null); player.Stop(); player.Dispose(); player.Dispose(); player.SetSource(path);
            Assert.False(backend.HasSource);
        }
        finally { window.Close(); File.Delete(path); }
    }

    [Fact]
    public void Windows_decodes_owned_audio_seeks_without_opening_output_and_releases_on_replacement()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Native Media Foundation decoder requires Windows.");
        var fixture = Environment.GetEnvironmentVariable("VEMO_PLAYBACK_FIXTURE");
        // CI may supply a real AAC fixture; a PCM fixture always exercises the native reader.
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(16000, 16, 1)))
            for (int i = 0; i < 16000 * 3; i++) writer.WriteSample((float)Math.Sin(i * Math.PI * 440 / 8000) * .25f);
        try
        {
            foreach (var input in new[] { path }.Concat(fixture?.Split('|') ?? []))
            {
                using var audio = new AudioPlayback(); audio.SetSource(input);
                Assert.Null(audio.Error); Assert.True(audio.Duration.TotalSeconds > 1);
                Console.WriteLine($"Native decoder: {Path.GetFileName(input)}, duration={audio.Duration.TotalSeconds:F3}s; output remains unopened.");
                Assert.False(audio.IsPlaying);
                audio.Seek(TimeSpan.FromSeconds(audio.Duration.TotalSeconds / 2));
                Assert.InRange(audio.Position.TotalSeconds, audio.Duration.TotalSeconds / 2 - .1, audio.Duration.TotalSeconds / 2 + .1);
                var decoder = (MediaFoundationReader)typeof(AudioPlayback).GetField("reader", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(audio)!;
                var bytes = new byte[4096];
                Assert.True(decoder.Read(bytes, 0, bytes.Length) > 0);
                Assert.Contains(bytes, b => b != 0);
                audio.Seek(TimeSpan.MaxValue); Assert.InRange(audio.Position.TotalSeconds, audio.Duration.TotalSeconds - .1, audio.Duration.TotalSeconds + .1);
                audio.Pause(); audio.Stop(); Assert.Equal(TimeSpan.Zero, audio.Position);
                Assert.Null(typeof(AudioPlayback).GetField("output", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(audio));
                audio.SetSource(path + ".missing"); Assert.False(audio.HasSource); Assert.NotNull(audio.Error);
                audio.SetSource(input); Assert.True(audio.HasSource);
                audio.SetSource(null); Assert.False(audio.HasSource); Assert.Equal(TimeSpan.Zero, audio.Duration);
            }
            using var unlocked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { File.Delete(path); }
    }
}
