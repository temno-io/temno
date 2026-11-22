using NAudio.CoreAudioApi;
using NAudio.Wave;
using Temno.Media.Audio;

namespace Temno.App.Audio;

/// <summary>
/// Windows-only render-device bridge for a mixed timeline program. The caller owns timeline
/// transport; this type only translates its frame positions into output PCM positions.
/// </summary>
internal sealed class TimelineAudioPlayback : IDisposable
{
    private WasapiOut? _output;
    private TimelinePcmWaveProvider? _provider;

    public bool IsReady => _output is not null && _provider is not null;

    public event Action<string>? DeviceError;

    public bool TryLoad(TimelineAudioProgram program, out string? error)
    {
        ArgumentNullException.ThrowIfNull(program);
        Clear();
        try
        {
            _provider = new TimelinePcmWaveProvider(program);
            _output = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 80);
            _output.PlaybackStopped += Output_PlaybackStopped;
            _output.Init(_provider);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Clear();
            return false;
        }
    }

    public void PlayFromFrame(int frame)
    {
        if (_output is null || _provider is null) return;
        _provider.ClearLimit();
        _provider.SeekToFrame(frame);
        _output.Play();
    }

    public void PauseAtFrame(int frame)
    {
        if (_output is null || _provider is null) return;
        _output.Pause();
        _provider.SeekToFrame(frame);
    }

    public void SeekToFrame(int frame, bool resume)
    {
        if (_output is null || _provider is null) return;
        _output.Stop();
        _provider.ClearLimit();
        _provider.SeekToFrame(frame);
        if (resume)
            _output.Play();
    }

    public void PlayScrubAtFrame(int frame, TimeSpan? duration = null)
    {
        if (_output is null || _provider is null) return;
        _output.Stop();
        _provider.SeekToFrame(frame);
        _provider.LimitToSampleFrames(Math.Max(1, (int)Math.Ceiling(
            (duration ?? TimeSpan.FromMilliseconds(80)).TotalSeconds * _provider.WaveFormat.SampleRate)));
        _output.Play();
    }

    public void StopScrub()
    {
        if (_output is null || _provider is null) return;
        _output.Stop();
        _provider.ClearLimit();
    }

    public void Clear()
    {
        if (_output is not null)
        {
            _output.PlaybackStopped -= Output_PlaybackStopped;
            _output.Stop();
            _output.Dispose();
        }

        _output = null;
        _provider = null;
    }

    public void Dispose() => Clear();

    private void Output_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            DeviceError?.Invoke(e.Exception.Message);
    }

    private sealed class TimelinePcmWaveProvider : IWaveProvider
    {
        private readonly TimelineAudioProgram _program;
        private int _position;
        private int? _limit;

        public TimelinePcmWaveProvider(TimelineAudioProgram program)
        {
            _program = program;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(program.SampleRate, program.ChannelCount);
        }

        public WaveFormat WaveFormat { get; }

        public void SeekToFrame(int frame)
        {
            _position = _program.InterleavedSampleOffsetForFrame(frame);
            _limit = null;
        }

        public void LimitToSampleFrames(int sampleFrames)
        {
            var sampleCount = checked(sampleFrames * WaveFormat.Channels);
            _limit = Math.Min(_program.Samples.Length, checked(_position + sampleCount));
        }

        public void ClearLimit() => _limit = null;

        public int Read(byte[] buffer, int offset, int count)
        {
            var end = Math.Min(_program.Samples.Length, _limit ?? _program.Samples.Length);
            var available = Math.Max(0, end - _position);
            var copied = Math.Min(count / sizeof(float), available);
            if (copied == 0) return 0;

            Buffer.BlockCopy(_program.Samples, _position * sizeof(float), buffer, offset, copied * sizeof(float));
            _position += copied;
            return copied * sizeof(float);
        }
    }
}
