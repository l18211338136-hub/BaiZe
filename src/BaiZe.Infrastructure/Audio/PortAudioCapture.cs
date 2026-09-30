using System.Runtime.InteropServices;
using System.Threading.Channels;
using BaiZe.Domain.Ports;
using PortAudioSharp;

namespace BaiZe.Infrastructure.Audio;

/// <summary>
/// 跨平台麦克风采集（PortAudio 真实实现）：默认输入设备，16kHz / 单声道，
/// 采集 Float32 样本 → 转 16bit PCM 小端 → 经有界 Channel 交给 CaptureAsync 消费者。
/// 可 Start/Stop 复用（每场会议一轮）。
/// </summary>
public sealed class PortAudioCapture : IAudioCapture
{
    private Channel<byte[]>? _channel;
    private PortAudioSharp.Stream? _stream;
    private bool _portAudioInitialized;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_stream is not null) return Task.CompletedTask;   // 已在采集

        if (!_portAudioInitialized)
        {
            PortAudio.Initialize();
            _portAudioInitialized = true;
        }

        var deviceIndex = PortAudio.DefaultInputDevice;
        if (deviceIndex == PortAudio.NoDevice)
            throw new InvalidOperationException("未找到可用的麦克风输入设备。");

        var info = PortAudio.GetDeviceInfo(deviceIndex);
        var inParams = new StreamParameters
        {
            device = deviceIndex,
            channelCount = 1,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = info.defaultLowInputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero
        };

        _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest   // 消费端卡顿时丢最旧块，保实时
        });

        PortAudioSharp.Stream.Callback callback = (IntPtr input, IntPtr output,
            uint frameCount, ref StreamCallbackTimeInfo timeInfo,
            StreamCallbackFlags statusFlags, IntPtr userData) =>
        {
            var samples = new float[frameCount];
            Marshal.Copy(input, samples, 0, (int)frameCount);

            var pcm = new byte[frameCount * 2];
            for (var i = 0; i < frameCount; i++)
            {
                var v = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * 32767f);
                pcm[i * 2] = (byte)(v & 0xFF);
                pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }
            _channel.Writer.TryWrite(pcm);
            return StreamCallbackResult.Continue;
        };

        _stream = new PortAudioSharp.Stream(inParams: inParams, outParams: null,
            sampleRate: 16000, framesPerBuffer: 0,
            streamFlags: StreamFlags.ClipOff, callback: callback, userData: IntPtr.Zero);
        _stream.Start();
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<byte[]> CaptureAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_channel is null) yield break;
        await foreach (var chunk in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return chunk;
    }

    public Task StopAsync()
    {
        try
        {
            _stream?.Stop();
            _stream?.Dispose();
        }
        catch { /* 设备被占用/拔出时忽略，保 UI 不崩 */ }
        _stream = null;
        _channel?.Writer.TryComplete();
        _channel = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (_portAudioInitialized)
        {
            PortAudio.Terminate();
            _portAudioInitialized = false;
        }
    }
}
