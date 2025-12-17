using System;

namespace ScreenRecorder.App.Services.Audio;

internal static class AudioMeter
{
    public static float ComputeRms(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        double sumSq = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var s = samples[i];
            sumSq += s * s;
        }

        return (float)Math.Sqrt(sumSq / samples.Length);
    }
}
