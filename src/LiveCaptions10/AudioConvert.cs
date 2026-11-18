using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LiveCaptions10;

internal static class AudioConvert
{
    static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>
    /// WASAPI reports WAVE_FORMAT_EXTENSIBLE; describe the same bytes with a plain PCM / float
    /// format so NAudio's sample converters accept it.
    /// </summary>
    public static WaveFormat Simplify(WaveFormat format)
    {
        if (format is WaveFormatExtensible ext)
        {
            if (ext.SubFormat == SubtypeIeeeFloat) return WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);
            if (ext.SubFormat == SubtypePcm) return new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels);
        }
        return format;
    }

    /// <summary>Any PCM / float source → 16 kHz mono float.</summary>
    public static ISampleProvider ToRecognizerFormat(IWaveProvider source)
    {
        ISampleProvider samples = source.ToSampleProvider();
        if (samples.WaveFormat.Channels != 1) samples = new DownmixToMono(samples);
        if (samples.WaveFormat.SampleRate != AudioPipeline.SampleRate)
            samples = new WdlResamplingSampleProvider(samples, AudioPipeline.SampleRate);
        return samples;
    }

    public static void ToPcm16(float[] source, short[] destination, int count)
    {
        for (int i = 0; i < count; i++)
        {
            float v = source[i] * 32767f;
            destination[i] = v >= 32767f ? short.MaxValue : v <= -32768f ? short.MinValue : (short)v;
        }
    }
}

/// <summary>
/// Push-style converter: raw device packets go in, 16 kHz mono floats come out into a FIFO.
/// Converting as packets arrive (instead of pulling on a timer) means no silence is ever
/// inserted into the middle of real audio.
/// </summary>
internal sealed class PacketConverter
{
    readonly BufferedWaveProvider raw;
    readonly ISampleProvider converted;
    readonly float[] scratch = new float[4096];

    public PacketConverter(WaveFormat deviceFormat)
    {
        raw = new BufferedWaveProvider(AudioConvert.Simplify(deviceFormat))
        {
            BufferDuration = TimeSpan.FromSeconds(1),
            DiscardOnBufferOverflow = true,
            ReadFully = false, // only real data; the resampler keeps its state between packets
        };
        converted = AudioConvert.ToRecognizerFormat(raw);
    }

    public void Push(byte[] packet, int count, FloatRing output, object outputLock)
    {
        raw.AddSamples(packet, 0, count);
        int n;
        while ((n = converted.Read(scratch, 0, scratch.Length)) > 0)
        {
            lock (outputLock) output.Write(scratch, n);
        }
    }
}

/// <summary>Fixed-capacity ring buffer of floats. Not thread-safe; callers lock.</summary>
internal sealed class FloatRing
{
    readonly float[] data;
    int head, count;

    public FloatRing(int capacity) => data = new float[capacity];

    public int Count => count;

    public void Write(float[] source, int n)
    {
        for (int i = 0; i < n; i++)
        {
            data[(head + count) % data.Length] = source[i];
            if (count < data.Length) count++;
            else head = (head + 1) % data.Length; // full: overwrite oldest
        }
    }

    /// <summary>Removes n samples and adds them onto destination[0..n).</summary>
    public void ReadAdd(float[] destination, int n)
    {
        n = Math.Min(n, count);
        for (int i = 0; i < n; i++) destination[i] += data[(head + i) % data.Length];
        Skip(n);
    }

    /// <summary>Removes n samples and copies them into destination[0..n). Returns how many.</summary>
    public int Read(float[] destination, int n)
    {
        n = Math.Min(n, count);
        for (int i = 0; i < n; i++) destination[i] = data[(head + i) % data.Length];
        Skip(n);
        return n;
    }

    public void Skip(int n)
    {
        n = Math.Min(n, count);
        head = (head + n) % data.Length;
        count -= n;
    }
}

/// <summary>
/// Downmixes any channel layout to mono. For 5.1 / 7.1 it uses front-left, front-right and centre
/// (where dialogue lives) and skips LFE and surrounds.
/// </summary>
internal sealed class DownmixToMono : ISampleProvider
{
    readonly ISampleProvider source;
    readonly int channels;
    readonly int[] used;
    float[] buffer = Array.Empty<float>();

    public DownmixToMono(ISampleProvider source)
    {
        this.source = source;
        channels = source.WaveFormat.Channels;
        used = channels >= 6 ? new[] { 0, 1, 2 } : BuildRange(channels);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] destination, int offset, int count)
    {
        int needed = count * channels;
        if (buffer.Length < needed) buffer = new float[needed];
        int read = source.Read(buffer, 0, needed);
        int frames = read / channels;
        float scale = 1f / used.Length;
        for (int f = 0; f < frames; f++)
        {
            int baseIndex = f * channels;
            float sum = 0;
            foreach (int c in used) sum += buffer[baseIndex + c];
            destination[offset + f] = sum * scale;
        }
        return frames;
    }

    static int[] BuildRange(int n)
    {
        var r = new int[n];
        for (int i = 0; i < n; i++) r[i] = i;
        return r;
    }
}
