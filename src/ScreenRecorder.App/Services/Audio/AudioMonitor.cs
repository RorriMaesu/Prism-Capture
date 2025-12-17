using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ScreenRecorder.App.Services.Audio;

internal sealed class AudioMonitor : IDisposable
{
    private readonly WasapiMixedAudioSource _source;
    private WasapiOut? _out;

    public AudioMonitor(WasapiMixedAudioSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public bool IsRunning => _out is not null;

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        if (!_source.IsRunning)
        {
            _source.StartDefault();
        }

        // Low latency shared-mode output.
        _out = new WasapiOut(AudioClientShareMode.Shared, 50);

        // Pull mixed audio from the source.
        ISampleProvider sp = new SourceSampleProvider(_source);

        // Convert float samples to a wave provider.
        var waveProvider = new SampleToWaveProvider(sp);
        _out.Init(waveProvider);
        _out.Play();
    }

    public void Stop()
    {
        if (_out is null)
        {
            return;
        }

        try { _out.Stop(); } catch { }
        try { _out.Dispose(); } catch { }
        _out = null;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private sealed class SourceSampleProvider : ISampleProvider
    {
        private readonly WasapiMixedAudioSource _source;

        public SourceSampleProvider(WasapiMixedAudioSource source)
        {
            _source = source;
        }

        public WaveFormat WaveFormat => _source.Format;

        public int Read(float[] buffer, int offset, int count)
        {
            return _source.Read(buffer, offset, count);
        }
    }
}
