using Vemo.Diarization;
using Xunit;
namespace Vemo.Core.Tests;
public sealed class SpeakerProbeOptionsTests
{
    [Fact] public void OmittedControlsPreserveAutomaticDefaults()
        => Assert.Equal(new SpeakerProbeOptions(0.5f, -1), SpeakerProbeOptions.Parse([]));
    [Fact] public void ExplicitControlsAreInvariantAndIndependent()
    {
        Assert.Equal(new SpeakerProbeOptions(0.8f, 3), SpeakerProbeOptions.Parse(["--num-clusters", "3", "--threshold", "0.8"]));
        Assert.Equal(new SpeakerProbeOptions(0.9f, -1), SpeakerProbeOptions.Parse(["--threshold", "0.9"]));
        Assert.Equal(new SpeakerProbeOptions(0.5f, 1), SpeakerProbeOptions.Parse(["--num-clusters", "1"]));
        Assert.Equal(20, SpeakerProbeOptions.Parse(["--num-clusters", "20"]).NumClusters);
    }
    [Theory]
    [InlineData("--threshold", "NaN")]
    [InlineData("--threshold", "Infinity")]
    [InlineData("--threshold", "0")]
    [InlineData("--threshold", "-0.1")]
    [InlineData("--threshold", "2.01")]
    [InlineData("--threshold", "0,8")]
    [InlineData("--num-clusters", "0")]
    [InlineData("--num-clusters", "-1")]
    [InlineData("--num-clusters", "21")]
    [InlineData("--num-clusters", "1.5")]
    [InlineData("--unknown", "1")]
    public void InvalidControlsFailBeforeNativeLoading(string name, string value)
        => Assert.Throws<ArgumentException>(() => SpeakerProbeOptions.Parse([name, value]));
    [Fact] public void DuplicateAndIncompleteControlsRejected()
    {
        Assert.Throws<ArgumentException>(() => SpeakerProbeOptions.Parse(["--threshold"]));
        Assert.Throws<ArgumentException>(() => SpeakerProbeOptions.Parse(["--threshold", "0.8", "--threshold", "0.9"]));
    }
}
