using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ScreenRecorder.App.Helpers;

namespace ScreenRecorder.App.Services.Audio;

internal sealed class WasapiMixedAudioSource : IDisposable
{
    private readonly object _gate = new();

    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _loopbackCapture;

    private BufferedWaveProvider? _micBuffer;
    private BufferedWaveProvider? _loopbackBuffer;

    private ISampleProvider? _mixed;

    private readonly WaveFormat _targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

    private long _micBytes;
    private long _sysBytes;

    public bool IsRunning { get; private set; }

    public float MicRms { get; private set; }
    public float SystemRms { get; private set; }

    public float MicGain { get; set; } = 1.0f;
    public float SystemGain { get; set; } = 1.0f;

    public WaveFormat Format => _targetFormat;

    public void StartDefault()
    {
        lock (_gate)
        {
            Breadcrumbs.Session("WasapiMixedAudioSource: StartDefault");
            Stop_NoLock();

            MicRms = 0;
            SystemRms = 0;

            // Mic (optional)
            try
            {
                _micCapture = new WasapiCapture();
                _micCapture.ShareMode = AudioClientShareMode.Shared;
                _micCapture.DataAvailable += OnMicData;
                _micCapture.RecordingStopped += OnCaptureStopped;

                _micBuffer = new BufferedWaveProvider(_micCapture.WaveFormat)
                {
                    DiscardOnBufferOverflow = true
                };

                Breadcrumbs.Write($"WASAPI mic: ok format={_micCapture.WaveFormat}");
            }
            catch
            {
                _micCapture = null;
                _micBuffer = null;
                Breadcrumbs.Write("WASAPI mic: unavailable");
            }

            // System loopback (optional)
            try
            {
                _loopbackCapture = new WasapiLoopbackCapture();
                _loopbackCapture.DataAvailable += OnLoopbackData;
                _loopbackCapture.RecordingStopped += OnCaptureStopped;

                _loopbackBuffer = new BufferedWaveProvider(_loopbackCapture.WaveFormat)
                {
                    DiscardOnBufferOverflow = true
                };

                Breadcrumbs.Write($"WASAPI loopback: ok format={_loopbackCapture.WaveFormat}");
            }
            catch
            {
                _loopbackCapture = null;
                _loopbackBuffer = null;
                Breadcrumbs.Write("WASAPI loopback: unavailable");
            }

            // Build sample graph from whichever sources are available.
            var inputs = new System.Collections.Generic.List<ISampleProvider>(2);
            if (_micBuffer is not null && _micCapture is not null)
            {
                inputs.Add(BuildProvider(_micBuffer, _micCapture.WaveFormat, () => MicGain));
            }
            if (_loopbackBuffer is not null && _loopbackCapture is not null)
            {
                inputs.Add(BuildProvider(_loopbackBuffer, _loopbackCapture.WaveFormat, () => SystemGain));
            }

            _mixed = inputs.Count switch
            {
                0 => new SilenceSampleProvider(_targetFormat),
                1 => inputs[0],
                _ => new MixingSampleProvider(inputs) { ReadFully = true }
            };

            Breadcrumbs.Write($"WASAPI mixed format={_targetFormat} inputs={inputs.Count}");

            try { _micCapture?.StartRecording(); } catch { }
            try { _loopbackCapture?.StartRecording(); } catch { }

            IsRunning = true;
            Breadcrumbs.Write("WASAPI: running=true");
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var mixed = _mixed;
        if (!IsRunning || mixed is null)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        var read = mixed.Read(buffer, offset, count);
        if (read < count)
        {
            Array.Clear(buffer, offset + read, count - read);
            read = count;
        }

        // meters (computed on the chunk we just produced)
        MicRms = MicRms; // updated in callbacks
        SystemRms = SystemRms;

        return read;
    }

    private ISampleProvider BuildProvider(BufferedWaveProvider source, WaveFormat sourceFormat, Func<float> gainGetter)
    {
        // Convert buffered bytes -> sample provider
        ISampleProvider sample = source.ToSampleProvider();

        // Resample to 48k
        if (sample.WaveFormat.SampleRate != _targetFormat.SampleRate)
        {
            sample = new WdlResamplingSampleProvider(sample, _targetFormat.SampleRate);
        }

        // Ensure stereo
        if (sample.WaveFormat.Channels == 1)
        {
            sample = new MonoToStereoSampleProvider(sample);
        }
        else if (sample.WaveFormat.Channels > 2)
        {
            sample = new StereoToMonoSampleProvider(sample);
            sample = new MonoToStereoSampleProvider(sample);
        }

        var volume = new VolumeSampleProvider(sample);
        return new DelegatingSampleProvider(volume, gainGetter);
    }

    private void OnMicData(object? sender, WaveInEventArgs e)
    {
        if (_micBuffer is null)
        {
            return;
        }

        try
        {
            _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            Interlocked.Add(ref _micBytes, e.BytesRecorded);

            // Compute RMS on the fly if the mic stream is float; otherwise best-effort.
            MicRms = EstimateRms(e.Buffer, e.BytesRecorded, _micCapture?.WaveFormat);
        }
        catch (Exception ex)
        {
            Breadcrumbs.Write("WASAPI mic callback exception");
            Breadcrumbs.Write(ex);
        }
    }

    private void OnLoopbackData(object? sender, WaveInEventArgs e)
    {
        if (_loopbackBuffer is null)
        {
            return;
        }

        try
        {
            _loopbackBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            Interlocked.Add(ref _sysBytes, e.BytesRecorded);

            SystemRms = EstimateRms(e.Buffer, e.BytesRecorded, _loopbackCapture?.WaveFormat);
        }
        catch (Exception ex)
        {
            Breadcrumbs.Write("WASAPI loopback callback exception");
            Breadcrumbs.Write(ex);
        }
    }

    private static float EstimateRms(byte[] buffer, int bytes, WaveFormat? fmt)
    {
        if (fmt is null)
        {
            return 0;
        }

        // Most modern devices provide IEEE float. If not, return 0 for now.
        if (fmt.Encoding != WaveFormatEncoding.IeeeFloat)
        {
            return 0;
        }

        var floats = bytes / 4;
        if (floats <= 0)
        {
            return 0;
        }

        // Cheap conversion: interpret as float32 little-endian.
        var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, floats * 4));
        return AudioMeter.ComputeRms(span);
    }

    private void OnCaptureStopped(object? sender, StoppedEventArgs e)
    {
        // no-op; stop is controlled by Stop()
    }

    public void Stop()
    {
        lock (_gate)
        {
            Breadcrumbs.Session("WasapiMixedAudioSource: Stop");
            Stop_NoLock();
            Breadcrumbs.Write("WASAPI: running=false");
        }
    }

    private void Stop_NoLock()
    {
        IsRunning = false;

        try
        {
            _micCapture?.StopRecording();
        }
        catch { }

        try
        {
            _loopbackCapture?.StopRecording();
        }
        catch { }

        if (_micCapture is not null)
        {
            _micCapture.DataAvailable -= OnMicData;
            _micCapture.RecordingStopped -= OnCaptureStopped;
        }

        if (_loopbackCapture is not null)
        {
            _loopbackCapture.DataAvailable -= OnLoopbackData;
            _loopbackCapture.RecordingStopped -= OnCaptureStopped;
        }

        _micCapture?.Dispose();
        _micCapture = null;

        _loopbackCapture?.Dispose();
        _loopbackCapture = null;

        _micBuffer = null;
        _loopbackBuffer = null;

        _mixed = null;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private sealed class DelegatingSampleProvider : ISampleProvider
    {
        private readonly VolumeSampleProvider _volume;
        private readonly Func<float> _gain;

        public DelegatingSampleProvider(VolumeSampleProvider volume, Func<float> gain)
        {
            _volume = volume;
            _gain = gain;
        }

        public WaveFormat WaveFormat => _volume.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            _volume.Volume = Math.Clamp(_gain(), 0f, 2f);
            return _volume.Read(buffer, offset, count);
        }
    }

    private sealed class SilenceSampleProvider : ISampleProvider
    {
        public SilenceSampleProvider(WaveFormat waveFormat)
        {
            WaveFormat = waveFormat;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }
    }
}
