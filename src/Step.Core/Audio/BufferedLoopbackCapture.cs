namespace Step.Core.Audio;

using NAudio.CoreAudioApi;
using NAudio.Wave;

/// <summary>
/// WASAPI loopback 捕获，允许指定捕获缓冲长度（NAudio 的 WasapiLoopbackCapture 固定 100ms）。
/// 通过继承 WasapiCapture 并加上 Loopback 标志实现，避免反射改私有字段导致缓冲错位。
/// </summary>
public sealed class BufferedLoopbackCapture : WasapiCapture
{
    public BufferedLoopbackCapture(MMDevice device, int bufferMilliseconds)
        : base(device, false, Math.Clamp(bufferMilliseconds, 10, 500))
    {
    }

    protected override AudioClientStreamFlags GetAudioClientStreamFlags()
    {
        return base.GetAudioClientStreamFlags() | AudioClientStreamFlags.Loopback;
    }
}
