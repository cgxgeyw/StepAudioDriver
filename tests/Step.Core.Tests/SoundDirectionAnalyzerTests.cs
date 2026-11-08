using Step.Core.Audio;
using Xunit;

namespace Step.Core.Tests;

public class SoundDirectionAnalyzerTests
{
    [Fact]
    public void FrontLeft_IsLeftAndFront()
    {
        var levels = new float[8];
        levels[0] = 0.3f;

        var direction = SoundDirectionAnalyzer.Analyze(levels);

        Assert.True(direction.X < 0);
        Assert.True(direction.Y > 0);
        Assert.True(direction.Confidence > 0.9f);
    }

    [Fact]
    public void BackRight_IsRightAndBack()
    {
        var levels = new float[8];
        levels[5] = 0.3f;

        var direction = SoundDirectionAnalyzer.Analyze(levels);

        Assert.True(direction.X > 0);
        Assert.True(direction.Y < 0);
    }

    [Fact]
    public void Center_IsFront()
    {
        var levels = new float[8];
        levels[2] = 0.3f;

        var direction = SoundDirectionAnalyzer.Analyze(levels);

        Assert.Equal(0f, direction.X, 3);
        Assert.True(direction.Y > 0.9f);
    }

    [Fact]
    public void LfeOnly_HasNoDirection()
    {
        var levels = new float[8];
        levels[3] = 0.3f;

        var direction = SoundDirectionAnalyzer.Analyze(levels);

        Assert.Equal(0f, direction.Confidence, 3);
        Assert.Equal(0f, direction.X, 3);
        Assert.Equal(0f, direction.Y, 3);
    }

    [Fact]
    public void BalancedStereo_IsCentered()
    {
        var levels = new float[8];
        levels[0] = 0.3f;
        levels[1] = 0.3f;

        var direction = SoundDirectionAnalyzer.Analyze(levels);

        Assert.Equal(0f, direction.X, 3);
        Assert.True(direction.Y > 0.8f);
    }
}
