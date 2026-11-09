using Step.Core.Audio.DSP;
using Xunit;

namespace Step.Core.Tests;

public class Equalizer15BandTests
{
    private static readonly float[] Frequencies15 =
    {
        25, 40, 63, 100, 160, 250, 400, 630, 1000,
        1600, 2500, 4000, 6300, 10000, 16000
    };

    [Fact]
    public void SetAllBands_ActivatesFifteenBands()
    {
        var engine = new EqualizerEngine();
        var gains = new float[15];
        var qs = new float[15];
        Array.Fill(qs, 2.0f);
        gains[8] = 6f;

        engine.SetAllBands(gains, Frequencies15, qs);

        Assert.Equal(15, engine.ActiveBandCount);

        var response = engine.GetMagnitudeResponseDb(new[] { 1000f, 2000f });
        Assert.True(response[0] > 5f, $"1k 应有明显提升，实际 {response[0]:F2} dB");
        Assert.True(response[1] < 2f, $"2k 应基本回落，实际 {response[1]:F2} dB");
    }

    [Fact]
    public void SetAllBands_CanSwitchBackToTenBands()
    {
        var engine = new EqualizerEngine();
        engine.SetAllBands(new float[15], Frequencies15, Enumerable.Repeat(1f, 15).ToArray());

        engine.SetAllBands(
            new float[10],
            new float[] { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 },
            Enumerable.Repeat(1f, 10).ToArray());

        Assert.Equal(10, engine.ActiveBandCount);
    }
}
