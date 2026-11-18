using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace LiveCaptions10;

/// <summary>
/// Captures what the PC is playing (WASAPI loopback) and, optionally, the default microphone,
/// mixes them and hands 16 kHz mono 16-bit frames to the recognizer on a dedicated thread.
/// Silence is synthesized when nothing is playing (loopback delivers no packets then), so the
/// recognizer always sees a continuous stream and can finish the last sentence.
/// </summary>
internal sealed class AudioPipeline : IDisposable
{
    public const int SampleRate = 16000;

    /// <summary>Raised on the audio thread. The buffer is reused between calls.</summary>
    public event Action<short[], int>? AudioFrame;

    /// <summary>Human-readable description of the devices in use, and whether it is an error.</summary>
    public event Action<string, bool>? StatusChanged;

    readonly object opLock = new();
    Task lastOp = Task.CompletedTask;

    // Everything below is only touched from queued operations (thread-pool / MTA), never the UI thread.
    MMDeviceEnumerator? enumerator;
    DeviceWatcher? watcher;
    CaptureSource? loopback, mic;
    bool running;
    volatile bool includeMic;

    const int MaxBacklogSeconds = 8;

    volatile CaptureSource[] active = Array.Empty<CaptureSource>();
    readonly FloatRing mixed = new(SampleRate * 30);
    readonly object mixedLock = new();
    readonly AutoResetEvent mixedReady = new(false);
    Thread? pumpThread, feedThread;
    volatile bool pumping;
    int restartToken;

    public Task StartAsync(bool includeMicrophone) => Enqueue(() =>
    {
        includeMic = includeMicrophone;
        if (running) return;
        running = true;
        EnsureEnumerator();
        OpenLoopback();
        if (includeMic) OpenMic();
        ReportStatus();
        StartPump();
    });

    public Task StopAsync() => Enqueue(() =>
    {
        if (!running) return;
        running = false;
        StopPump();
        Close(ref loopback);
        Close(ref mic);
    });

    public Task SetIncludeMicrophoneAsync(bool on) => Enqueue(() =>
    {
        includeMic = on;
        if (!running) return;
        if (on && mic == null) OpenMic();
        else if (!on) Close(ref mic);
        ReportStatus();
    });

    public void Dispose()
    {
        try
        {
            StopAsync().Wait(3000);
            Enqueue(() =>
            {
                if (enumerator != null && watcher != null) enumerator.UnregisterEndpointNotificationCallback(watcher);
                enumerator?.Dispose();
                enumerator = null;
            }).Wait(2000);
        }
        catch (Exception ex)
        {
            Log.Error("Disposing audio", ex);
        }
    }

    /// <summary>Runs device operations one at a time, in order, on a thread-pool (MTA) thread.</summary>
    Task Enqueue(Action op)
    {
        lock (opLock)
        {
            lastOp = lastOp.ContinueWith(_ =>
            {
                try
                {
                    op();
                }
                catch (Exception ex)
                {
                    Log.Error("Audio operation failed", ex);
                    StatusChanged?.Invoke("Audio error: " + ex.Message, true);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return lastOp;
        }
    }

    void EnsureEnumerator()
    {
        if (enumerator != null) return;
        enumerator = new MMDeviceEnumerator();
        watcher = new DeviceWatcher(OnDefaultDeviceChanged);
        enumerator.RegisterEndpointNotificationCallback(watcher);
    }

    void OpenLoopback()
    {
        try
        {
            var device = enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            loopback = new CaptureSource(device, isLoopback: true, OnSourceFailed);
            loopback.Start();
            Log.Info("Capturing output: " + loopback.DeviceName);
        }
        catch (Exception ex)
        {
            Log.Error("Opening loopback capture failed", ex);
            loopback?.Dispose();
            loopback = null;
        }
        UpdateActive();
    }

    void OpenMic()
    {
        try
        {
            var device = enumerator!.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            mic = new CaptureSource(device, isLoopback: false, OnSourceFailed);
            mic.Start();
            Log.Info("Capturing microphone: " + mic.DeviceName);
        }
        catch (Exception ex)
        {
            Log.Error("Opening microphone failed", ex);
            mic?.Dispose();
            mic = null;
        }
        UpdateActive();
    }

    void Close(ref CaptureSource? source)
    {
        var old = source;
        source = null;
        UpdateActive();
        old?.Dispose();
    }

    void UpdateActive() => active = new[] { loopback, mic }.OfType<CaptureSource>().ToArray();

    void ReportStatus()
    {
        if (!running) return;
        if (loopback == null)
        {
            StatusChanged?.Invoke("Can't listen to this PC's audio. Check that a playback device is enabled.", true);
            return;
        }
        var text = "Listening to " + loopback.DeviceName;
        if (includeMic) text += mic != null ? " + " + mic.DeviceName : " (microphone unavailable)";
        StatusChanged?.Invoke(text, false);
    }

    // Called on a COM notification thread: never touch devices here, only schedule a restart.
    void OnDefaultDeviceChanged(DataFlow flow)
    {
        if (flow == DataFlow.Capture && !includeMic) return;
        ScheduleRestart(TimeSpan.FromMilliseconds(500));
    }

    void OnSourceFailed(CaptureSource source, Exception error)
    {
        Log.Error("Capture stopped unexpectedly: " + source.DeviceName, error);
        ScheduleRestart(TimeSpan.FromMilliseconds(1500));
    }

    void ScheduleRestart(TimeSpan delay)
    {
        int token = Interlocked.Increment(ref restartToken);
        Task.Delay(delay).ContinueWith(_ =>
        {
            if (token == Volatile.Read(ref restartToken)) Enqueue(RestartDevices);
        }, TaskScheduler.Default);
    }

    void RestartDevices()
    {
        if (!running) return;
        Close(ref loopback);
        Close(ref mic);
        OpenLoopback();
        if (includeMic) OpenMic();
        ReportStatus();
    }

    // Two threads: the real-time mixer (never blocks, so device queues never overflow) and the
    // recognizer feeder (may stall while Vosk works in bursts; it catches up from the 30 s queue).
    void StartPump()
    {
        lock (mixedLock) mixed.Skip(mixed.Count);
        pumping = true;
        pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = "Live captions mixer", Priority = ThreadPriority.AboveNormal };
        feedThread = new Thread(FeedLoop) { IsBackground = true, Name = "Live captions recognizer" };
        pumpThread.Start();
        feedThread.Start();
    }

    void StopPump()
    {
        pumping = false;
        mixedReady.Set();
        pumpThread?.Join(2000);
        feedThread?.Join(5000);
        pumpThread = feedThread = null;
    }

    void FeedLoop()
    {
        const int chunk = SampleRate / 10;
        var floats = new float[chunk];
        var pcm = new short[chunk];
        while (pumping)
        {
            mixedReady.WaitOne(250);
            while (pumping)
            {
                int n;
                lock (mixedLock)
                {
                    if (mixed.Count > SampleRate * MaxBacklogSeconds)
                    {
                        Log.Info($"Recognizer fell {mixed.Count / SampleRate} s behind; skipping ahead");
                        mixed.Skip(mixed.Count - SampleRate);
                    }
                    if (mixed.Count < chunk) break;
                    n = mixed.Read(floats, chunk);
                }
                AudioConvert.ToPcm16(floats, pcm, n);
                try
                {
                    AudioFrame?.Invoke(pcm, n);
                }
                catch (Exception ex)
                {
                    Log.Error("Recognizer failed", ex);
                }
            }
        }
    }

    void PumpLoop()
    {
        var mix = new float[SampleRate];
        var clock = Stopwatch.StartNew();
        long consumed = 0;

        while (pumping)
        {
            Thread.Sleep(30);
            long target = (long)(clock.Elapsed.TotalSeconds * SampleRate);
            long due = target - consumed;
            if (due < SampleRate / 50) continue; // wait for at least 20 ms
            if (due > SampleRate / 2)
            {
                // We fell far behind (PC slept, debugger, ...). Skip ahead instead of catching up.
                consumed = target - SampleRate / 10;
                due = SampleRate / 10;
            }

            int n = (int)due;
            Array.Clear(mix, 0, n);
            foreach (var source in active) source.MixInto(mix, n);
            consumed += n;

            lock (mixedLock) mixed.Write(mix, n);
            mixedReady.Set();
        }
    }
}

/// <summary>
/// One capture device (loopback or microphone), converted to 16 kHz mono as packets arrive and
/// held in a small jitter buffer that the mixer drains in real time.
/// </summary>
internal sealed class CaptureSource : IDisposable
{
    const int PrimeSamples = AudioPipeline.SampleRate / 10;      // start reading once 100 ms is queued
    const int MaxQueued = AudioPipeline.SampleRate;              // safety valve: trim back if 1 s piles up

    readonly WasapiCapture capture;
    readonly PacketConverter converter;
    readonly FloatRing queue = new(AudioPipeline.SampleRate * 2);
    readonly object gate = new();
    bool primed;
    volatile bool disposed;

    public CaptureSource(MMDevice device, bool isLoopback, Action<CaptureSource, Exception> onFailed)
    {
        DeviceName = device.FriendlyName;
        capture = isLoopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device, true, 50);
        try
        {
            converter = new PacketConverter(capture.WaveFormat);
        }
        catch
        {
            capture.Dispose();
            throw;
        }

        capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded > 0) converter.Push(e.Buffer, e.BytesRecorded, queue, gate);
        };
        capture.RecordingStopped += (_, e) =>
        {
            if (!disposed && e.Exception != null) onFailed(this, e.Exception);
        };
    }

    public string DeviceName { get; }

    /// <summary>Times the jitter buffer ran dry (normal when playback stops; frequent = glitches).</summary>
    public int Underruns { get; private set; }

    public void Start() => capture.StartRecording();

    /// <summary>
    /// Adds the next <paramref name="count"/> samples onto <paramref name="mix"/>. Contributes silence
    /// while (re)filling the jitter buffer, e.g. when nothing is playing and loopback sends no packets.
    /// </summary>
    public void MixInto(float[] mix, int count)
    {
        lock (gate)
        {
            if (queue.Count > MaxQueued)
            {
                Log.Info($"{DeviceName}: dropped {(queue.Count - PrimeSamples) * 1000 / AudioPipeline.SampleRate} ms of queued audio (reader fell behind)");
                queue.Skip(queue.Count - PrimeSamples); // clock drift / stall
            }
            if (!primed)
            {
                if (queue.Count < PrimeSamples) return;
                primed = true;
            }
            int take = Math.Min(count, queue.Count);
            queue.ReadAdd(mix, take);
            if (take < count)
            {
                primed = false; // ran dry: audio stopped; re-prime when it resumes
                Underruns++;
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Log.Info($"Closing {DeviceName} ({Underruns} buffer underruns)");
        try
        {
            capture.StopRecording();
        }
        catch
        {
            // already stopped
        }
        capture.Dispose();
    }
}

internal sealed class DeviceWatcher : IMMNotificationClient
{
    readonly Action<DataFlow> onDefaultChanged;

    public DeviceWatcher(Action<DataFlow> onDefaultChanged) => this.onDefaultChanged = onDefaultChanged;

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        // Windows fires this once per role; react to one role per direction.
        if ((flow == DataFlow.Render && role == Role.Multimedia) || (flow == DataFlow.Capture && role == Role.Console))
            onDefaultChanged(flow);
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }

    public void OnDeviceAdded(string pwstrDeviceId) { }

    public void OnDeviceRemoved(string deviceId) { }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
}
