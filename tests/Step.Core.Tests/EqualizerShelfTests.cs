using Step.Core.Audio.DSP;
using Xunit;

namespace Step.Core.Tests;

public class EqualizerShelfTests
{
    [Fact]
    public void LowShelf_BoostsBassMoreThanMidrange()
    {
        var eq = new EqualizerEngine();
        eq.SetSampleRate(48000);
        eq.SetLowShelf(100f, 6f, 0.7f);

        var response = eq.GetMagnitudeResponseDb(new[] { 30f, 60f, 100f, 1000f });

        Assert.True(response[0] > 4.5f);
        Assert.True(response[1] > 4.0f);
        Assert.True(response[3] < 0.5f);
    }

    [Fact]
    public void HighShelf_BoostsTrebleMoreThanMidrange()
    {
        var eq = new EqualizerEngine();
        eq.SetSampleRate(48000);
        eq.SetHighShelf(8000f, 6f, 0.7f);

        var response = eq.GetMagnitudeResponseDb(new[] { 1000f, 8000f, 12000f, 16000f });

        Assert.True(response[0] < 0.5f);
        Assert.True(response[3] > 4.0f);
    }

    [Fact]
    public void IndependentQ_ChangesPeakWidth()
    {
        var wide = new EqualizerEngine();
        wide.SetSampleRate(48000);
        wide.SetAllBands(
            new[] { 0f, 0f, 0f, 0f, 6f, 0f, 0f, 0f, 0f, 0f },
            new[] { 32f, 64f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f },
            new[] { 1f, 1f, 1f, 1f, 0.7f, 1f, 1f, 1f, 1f, 1f });

        var narrow = new EqualizerEngine();
        narrow.SetSampleRate(48000);
        narrow.SetAllBands(
            new[] { 0f, 0f, 0f, 0f, 6f, 0f, 0f, 0f, 0f, 0f },
            new[] { 32f, 64f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f },
            new[] { 1f, 1f, 1f, 1f, 2.5f, 1f, 1f, 1f, 1f, 1f });

        var freqs = new[] { 350f, 500f, 700f };
        var wideResponse = wide.GetMagnitudeResponseDb(freqs);
        var narrowResponse = narrow.GetMagnitudeResponseDb(freqs);

        Assert.True(wideResponse[0] > narrowResponse[0]);
        Assert.True(wideResponse[2] > narrowResponse[2]);
    }
}
