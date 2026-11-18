using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace LiveCaptions10;

/// <summary>Wires audio → recognizer → caption window, plus the tray icon, menus and settings.</summary>
public partial class App : Application
{
    const string InstanceName = @"Local\LiveCaptions10";

    static readonly (string Name, double Size)[] TextSizes =
    {
        ("Small", 16), ("Medium", 20), ("Large", 26), ("Extra large", 34),
    };

    readonly AudioPipeline audio = new();
    AppSettings settings = new();
    CaptionWindow? window;
    SpeechEngine? engine;
    Forms.NotifyIcon? tray;
    Forms.ContextMenuStrip? gearMenu;
    Drawing.Icon? iconOn, iconOff;
    Mutex? instanceMutex;
    DispatcherTimer? saveTimer;
    CancellationTokenSource? downloadCts;
    ModelState modelState = ModelState.Loading;
    string modelError = "";
    double downloadProgress;
    string? audioInfo;
    bool audioError, captionsOn, exiting;
    int loadVersion;
    string? transcriptOverride;

    // Whisper second pass
    WhisperRefiner? refiner;
    WhisperState whisperState = WhisperState.Off;
    string whisperError = "";
    double whisperProgress;
    CancellationTokenSource? whisperCts;
    int whisperLoadVersion;
    readonly System.Collections.Generic.Dictionary<long, string> awaitingRefinement = new();

    enum ModelState { Missing, Downloading, DownloadFailed, Loading, LoadFailed, Ready }

    enum WhisperState { Off, Missing, Downloading, Failed, Loading, Ready }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unhandled UI exception", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            Log.Error("Fatal exception", ex.ExceptionObject as Exception);
            window?.ReleaseAppBar(); // never leave a reserved strip of screen behind
        };

        var args = e.Args;
        if (args.Length >= 3 && args[0] == "--selftest")
        {
            Shutdown(SelfTest.Run(args[1], args[2], args.Length > 3 ? args[3] : null));
            return;
        }
        if (args.Contains("--exit"))
        {
            if (EventWaitHandle.TryOpenExisting(InstanceName + ".Exit", out var running))
            {
                running.Set();
                running.Dispose();
            }
            Shutdown();
            return;
        }

        instanceMutex = new Mutex(true, InstanceName + ".Mutex", out bool firstInstance);
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        var exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Exit");
        if (!firstInstance)
        {
            showSignal.Set(); // starting it again just turns captions on in the running copy
            Shutdown();
            return;
        }
        ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => Dispatcher.InvokeAsync(() => SetCaptions(true)), null, Timeout.Infinite, false);
        ThreadPool.RegisterWaitForSingleObject(exitSignal, (_, _) => Dispatcher.InvokeAsync(ExitApp), null, Timeout.Infinite, true);

        int t = Array.IndexOf(args, "--transcript");
        if (t >= 0 && t + 1 < args.Length) transcriptOverride = Path.GetFullPath(args[t + 1]);

        settings = SettingsStore.Load();
        settings.Normalize();
        WhisperRuntime.Configure(settings.WhisperDevice);
        saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        saveTimer.Tick += (_, _) =>
        {
            saveTimer.Stop();
            SettingsStore.Save(settings);
        };

        window = new CaptionWindow(settings);
        window.ToggleRequested += ToggleCaptions;
        window.CloseRequested += () => SetCaptions(false);
        window.ActionRequested += OnStatusAction;
        window.SettingsMenuRequested += ShowSettingsMenu;
        window.LayoutChanged += RequestSave;

        int dump = Array.IndexOf(args, "--dump-audio");
        if (dump >= 0 && dump + 1 < args.Length)
        {
            // Debug: record exactly what the recognizer hears (16 kHz mono) to a WAV file.
            var writer = new NAudio.Wave.WaveFileWriter(Path.GetFullPath(args[dump + 1]), new NAudio.Wave.WaveFormat(AudioPipeline.SampleRate, 16, 1));
            var bytes = new byte[AudioPipeline.SampleRate * 2];
            audio.AudioFrame += (pcm, count) =>
            {
                Buffer.BlockCopy(pcm, 0, bytes, 0, count * 2);
                writer.Write(bytes, 0, count * 2);
                writer.Flush();
            };
        }
        audio.AudioFrame += (pcm, count) => Volatile.Read(ref engine)?.Accept(pcm, count);
        audio.StatusChanged += (text, isError) => Dispatcher.InvokeAsync(() =>
        {
            audioInfo = text;
            audioError = isError;
            UpdateStatus();
        });

        CreateTray();
        Log.Info("Started" + (window.HotkeyRegistered ? "" : " (Win+Ctrl+L is taken by another app)"));
        Log.Info("Data folder: " + SettingsStore.AppDataDir);
        SetCaptions(!args.Contains("--background"));
        _ = LoadModelAsync(settings.ModelPath);
        _ = LoadWhisperAsync();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        window?.ReleaseAppBar();
        SettingsStore.Save(settings);
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        window?.ReleaseAppBar();
        tray?.Dispose();
        base.OnExit(e);
    }

    // ---------- captions on / off ----------

    void ToggleCaptions() => SetCaptions(!captionsOn);

    void SetCaptions(bool on)
    {
        if (window == null || exiting) return;
        if (on == captionsOn)
        {
            if (on) window.ShowCaptions();
            return;
        }
        captionsOn = on;
        engine?.Reset();
        if (on)
        {
            window.ClearCaptions();
            window.ShowCaptions();
            _ = audio.StartAsync(settings.IncludeMicrophone);
        }
        else
        {
            _ = audio.StopAsync();
            window.HideCaptions();
        }
        UpdateTray();
    }

    void OnFinal(FinalUpdate update)
    {
        if (window == null) return;
        var (id, text) = window.ShowFinal(update);
        if (text.Length == 0) return;
        var whisper = refiner;
        if (whisper != null && settings.UseWhisper)
        {
            // Show the draft now; the final Whisper pass replaces it when ready. Its sanity checks compare
            // against Vosk's own words, which are independent of Whisper.
            awaitingRefinement[id] = text;
            whisper.Enqueue(id, CaptionFormatter.FormatFinal(update.Text, false), update.Audio);
        }
        else
        {
            AppendTranscript(text);
        }
    }

    void OnRefined(long id, string? refined)
    {
        awaitingRefinement.Remove(id, out var draft);
        if (refined == null)
        {
            if (draft != null) AppendTranscript(draft);
            return;
        }
        var text = CaptionFormatter.FormatRefined(refined, settings.FilterProfanity);
        window?.ReplaceSegment(id, text);
        if (text.Length > 0) AppendTranscript(text);
    }

    static string TranscriptsDir => Path.Combine(SettingsStore.AppDataDir, "transcripts");

    void AppendTranscript(string text)
    {
        string? path = transcriptOverride;
        if (path == null && settings.SaveTranscript) path = Path.Combine(TranscriptsDir, $"{DateTime.Now:yyyy-MM-dd}.txt");
        if (path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            Log.Error("Writing transcript", ex);
        }
    }

    // ---------- speech model ----------

    async Task LoadModelAsync(string? preferred)
    {
        int version = ++loadVersion;
        string? path = ModelManager.IsModel(preferred) ? preferred : ModelManager.FindModels().FirstOrDefault();
        if (path == null)
        {
            modelState = ModelState.Missing;
            UpdateStatus();
            return;
        }

        modelState = ModelState.Loading;
        UpdateStatus();
        try
        {
            var loaded = await Task.Run(() => new SpeechEngine(path));
            if (version != loadVersion || exiting)
            {
                loaded.Dispose(); // a newer load superseded this one
                return;
            }
            loaded.Partial += update => Dispatcher.InvokeAsync(() => window?.ShowPartial(update));
            loaded.Final += update => Dispatcher.InvokeAsync(() => OnFinal(update));
            Interlocked.Exchange(ref engine, loaded)?.Dispose();
            settings.ModelPath = path;
            RequestSave();
            modelState = ModelState.Ready;
            Log.Info("Loaded speech model " + path);
        }
        catch (Exception ex)
        {
            if (version != loadVersion) return;
            Log.Error("Loading speech model failed", ex);
            modelError = ex.Message;
            modelState = ModelState.LoadFailed;
        }
        UpdateStatus();
    }

    /// <summary>(Re)loads Whisper to match the settings: off, or final model + optional live model.</summary>
    async Task LoadWhisperAsync()
    {
        int version = ++whisperLoadVersion;
        var old = Interlocked.Exchange(ref refiner, null);
        if (old != null)
        {
            await Task.Run(old.Dispose);
            FlushAwaitingRefinement();
        }
        if (!settings.UseWhisper)
        {
            whisperState = WhisperState.Off;
            UpdateStatus();
            return;
        }
        if (ModelManager.ListWhisperModels().Count == 0)
        {
            whisperState = WhisperState.Missing;
            UpdateStatus();
            return;
        }

        whisperState = WhisperState.Loading;
        UpdateStatus();
        try
        {
            var chosenModel = settings.WhisperModel;
            bool live = settings.LiveWhisper;
            var loaded = await Task.Run(() =>
            {
                WhisperRuntime.EnsureLoaded();
                bool gpu = WhisperRuntime.OnGpu;
                // GPU: most accurate model for finished sentences, smallest (fastest) for live updates.
                // CPU: one small model, no live passes (they would starve the instant recognizer).
                var finalModel = ModelManager.FindWhisperModel(chosenModel, preferLarge: gpu)!;
                var liveModel = gpu && live ? ModelManager.ListWhisperModels()[0] : null;
                return new WhisperRefiner(finalModel, liveModel);
            });
            if (version != whisperLoadVersion || exiting || !settings.UseWhisper)
            {
                loaded.Dispose();
                return;
            }
            loaded.Refined += (id, text) => Dispatcher.InvokeAsync(() => OnRefined(id, text));
            loaded.LiveRefined += (snapshot, text) => Dispatcher.InvokeAsync(() => window?.ShowLive(snapshot, text));
            loaded.LiveSource = (utterance, length, growth) => Volatile.Read(ref engine)?.SnapshotLive(utterance, length, growth);
            refiner = loaded;
            whisperState = WhisperState.Ready;
            Log.Info($"Whisper on {WhisperRuntime.LoadedName}: {loaded.Description}");
        }
        catch (Exception ex)
        {
            Log.Error("Loading Whisper failed", ex);
            whisperError = ex.Message;
            whisperState = WhisperState.Failed;
        }
        UpdateStatus();
    }

    /// <summary>Sentences still waiting for Whisper when it is switched off keep their draft text.</summary>
    void FlushAwaitingRefinement()
    {
        foreach (var draft in awaitingRefinement.Values) AppendTranscript(draft);
        awaitingRefinement.Clear();
    }

    void ToggleWhisper()
    {
        if (whisperState == WhisperState.Downloading)
        {
            whisperCts?.Cancel();
            return;
        }
        settings.UseWhisper = !settings.UseWhisper;
        RequestSave();
        if (settings.UseWhisper && ModelManager.ListWhisperModels().Count == 0) StartWhisperDownload(ModelManager.WhisperDownloads[0]);
        else _ = LoadWhisperAsync();
    }

    void SetWhisperModel(string? fileName)
    {
        settings.WhisperModel = fileName;
        RequestSave();
        _ = LoadWhisperAsync();
    }

    void ToggleLiveWhisper()
    {
        settings.LiveWhisper = !settings.LiveWhisper;
        RequestSave();
        _ = LoadWhisperAsync();
    }

    /// <summary>The native Whisper library can't be swapped in a running process, so restart.</summary>
    void SetWhisperDevice(ComputeDevice device)
    {
        if (settings.WhisperDevice == device) return;
        settings.WhisperDevice = device;
        SettingsStore.Save(settings);
        RestartApp();
    }

    async void StartWhisperDownload(WhisperDownload download)
    {
        if (whisperState == WhisperState.Downloading) return;
        settings.UseWhisper = true;
        RequestSave();
        whisperCts = new CancellationTokenSource();
        whisperState = WhisperState.Downloading;
        whisperProgress = 0;
        UpdateStatus();
        var progress = new Progress<double>(p =>
        {
            whisperProgress = p;
            UpdateStatus();
        });
        try
        {
            await ModelManager.DownloadWhisperModelAsync(download, progress, whisperCts.Token);
            await LoadWhisperAsync();
            return;
        }
        catch (OperationCanceledException)
        {
            whisperState = refiner != null ? WhisperState.Ready : WhisperState.Off;
        }
        catch (Exception ex)
        {
            Log.Error("Whisper download failed", ex);
            whisperError = ex.Message;
            whisperState = WhisperState.Failed;
        }
        UpdateStatus();
    }

    string WhisperStatusText() => whisperState switch
    {
        WhisperState.Ready => $"Whisper on {WhisperRuntime.LoadedName}: {refiner?.Description}",
        WhisperState.Loading => "loading Whisper…",
        WhisperState.Downloading => $"downloading Whisper model {whisperProgress:0%}",
        WhisperState.Missing => "Whisper model not installed",
        WhisperState.Failed => "Whisper unavailable: " + whisperError,
        _ => "",
    };

    async void StartDownload()
    {
        if (modelState == ModelState.Downloading) return;
        downloadCts = new CancellationTokenSource();
        modelState = ModelState.Downloading;
        downloadProgress = 0;
        UpdateStatus();
        var progress = new Progress<double>(p =>
        {
            if (modelState != ModelState.Downloading) return;
            downloadProgress = p;
            UpdateStatus();
        });
        try
        {
            var path = await ModelManager.DownloadDefaultModelAsync(progress, downloadCts.Token);
            await LoadModelAsync(path);
            return;
        }
        catch (OperationCanceledException)
        {
            modelState = ModelState.Missing;
        }
        catch (Exception ex)
        {
            Log.Error("Model download failed", ex);
            modelError = ex.Message;
            modelState = ModelState.DownloadFailed;
        }
        UpdateStatus();
    }

    void UpdateStatus()
    {
        if (window == null) return;
        switch (modelState)
        {
            case ModelState.Missing:
                window.SetStatus($"Live captions needs English (US) speech files to work ({ModelManager.DefaultModelSize}). They stay on this PC.", "Download");
                break;
            case ModelState.Downloading:
                window.SetStatus($"Downloading speech files… {downloadProgress:0%}", "Cancel");
                break;
            case ModelState.DownloadFailed:
                window.SetStatus("Download failed: " + modelError, "Retry");
                break;
            case ModelState.Loading:
                window.SetStatus("Loading speech model…", null);
                break;
            case ModelState.LoadFailed:
                window.SetStatus("Couldn't load the speech model: " + modelError, "Open models folder");
                break;
            default:
                if (audioError)
                {
                    window.SetStatus(audioInfo ?? "Audio error", null);
                    break;
                }
                var status = "Ready to caption";
                if (audioInfo != null) status += "   ·   " + audioInfo;
                var whisper = WhisperStatusText();
                if (whisper.Length > 0) status += "   ·   " + whisper;
                window.SetStatus(status, null);
                break;
        }
    }

    void OnStatusAction()
    {
        switch (modelState)
        {
            case ModelState.Missing:
            case ModelState.DownloadFailed:
                StartDownload();
                break;
            case ModelState.Downloading:
                downloadCts?.Cancel();
                break;
            case ModelState.LoadFailed:
                OpenModelsFolder();
                break;
        }
    }

    // ---------- tray + menus ----------

    void CreateTray()
    {
        int size = Forms.SystemInformation.SmallIconSize.Width;
        iconOn = IconFactory.Create(size, active: true);
        iconOff = IconFactory.Create(size, active: false);

        var menu = NewMenu();
        menu.Opening += (_, e) =>
        {
            Populate(menu.Items);
            e.Cancel = false;
        };
        tray = new Forms.NotifyIcon { Icon = iconOff, Text = "Live captions", ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ToggleCaptions();
        };
    }

    void UpdateTray()
    {
        if (tray == null) return;
        tray.Icon = captionsOn ? iconOn : iconOff;
        tray.Text = captionsOn ? "Live captions: on" : "Live captions: off";
    }

    static Forms.ContextMenuStrip NewMenu() => new()
    {
        Renderer = new MenuRenderer(),
        Font = new Drawing.Font("Segoe UI", 9f),
        ShowImageMargin = true,
    };

    void ShowSettingsMenu(int x, int y)
    {
        gearMenu ??= NewMenu();
        Populate(gearMenu.Items);
        gearMenu.Show(new Drawing.Point(x, y));
    }

    void Populate(Forms.ToolStripItemCollection items)
    {
        items.Clear();
        items.Add(Item(captionsOn ? "Turn off live captions" : "Turn on live captions", ToggleCaptions,
            shortcut: window?.HotkeyRegistered == true ? "Win+Ctrl+L" : null));
        items.Add(new Forms.ToolStripSeparator());

        items.Add(Sub("Position",
            Item("Top", () => SetPosition(CaptionPosition.Top), settings.Position == CaptionPosition.Top),
            Item("Bottom", () => SetPosition(CaptionPosition.Bottom), settings.Position == CaptionPosition.Bottom),
            Item("Floating on screen", () => SetPosition(CaptionPosition.Floating), settings.Position == CaptionPosition.Floating)));
        items.Add(Sub("Caption style", CaptionStyles.All
            .Select(s => Item(s.Name, () => Change(() => settings.Style = s.Key), settings.Style == s.Key)).ToArray()));
        items.Add(Sub("Text size", TextSizes
            .Select(s => Item(s.Name, () => Change(() => settings.FontSize = s.Size), Math.Abs(settings.FontSize - s.Size) < 0.1)).ToArray()));
        items.Add(Sub("Lines shown", Enumerable.Range(1, 5)
            .Select(n => Item(n == 1 ? "1 line" : $"{n} lines", () => Change(() => settings.Lines = n), settings.Lines == n)).ToArray()));
        items.Add(new Forms.ToolStripSeparator());

        items.Add(WhisperMenuItem());
        items.Add(WhisperOptionsMenu());
        items.Add(Item("Include microphone audio", ToggleMicrophone, settings.IncludeMicrophone));
        items.Add(Item("Filter profanity", () => Change(() => settings.FilterProfanity = !settings.FilterProfanity), settings.FilterProfanity));
        items.Add(Sub("Transcripts",
            Item("Save transcripts", () => Change(() => settings.SaveTranscript = !settings.SaveTranscript), settings.SaveTranscript),
            Item("Open transcripts folder", () => OpenFolder(TranscriptsDir))));
        items.Add(Item("Start with Windows", ToggleStartup, SafeStartupEnabled()));
        items.Add(new Forms.ToolStripSeparator());

        items.Add(BuildLanguageMenu());
        items.Add(Item("Clear captions", () => window?.ClearCaptions(), enabled: captionsOn));
        items.Add(new Forms.ToolStripSeparator());
        items.Add(Item("Exit", ExitApp));
    }

    Forms.ToolStripMenuItem BuildLanguageMenu()
    {
        var models = ModelManager.FindModels();
        var menu = new Forms.ToolStripMenuItem("Language");
        foreach (var model in models)
        {
            bool current = string.Equals(engine?.ModelPath, model, StringComparison.OrdinalIgnoreCase);
            menu.DropDownItems.Add(Item(ModelManager.DisplayName(model), () => SwitchModel(model), current));
        }
        if (models.Count == 0)
        {
            menu.DropDownItems.Add(Item($"Download English (US) speech files ({ModelManager.DefaultModelSize})", StartDownload,
                enabled: modelState != ModelState.Downloading));
        }
        menu.DropDownItems.Add(new Forms.ToolStripSeparator());
        menu.DropDownItems.Add(Item("Get more languages… (unzip into the models folder)", () => OpenUrl(ModelManager.MoreModelsUrl)));
        menu.DropDownItems.Add(Item("Open models folder", OpenModelsFolder));
        return menu;
    }

    Forms.ToolStripMenuItem WhisperMenuItem()
    {
        var text = whisperState switch
        {
            WhisperState.Downloading => $"Downloading Whisper model… {whisperProgress:0%}  (click to cancel)",
            WhisperState.Loading => "High accuracy (Whisper): loading…",
            WhisperState.Failed => "High accuracy (Whisper): failed, click to retry",
            _ when ModelManager.ListWhisperModels().Count == 0 => $"High accuracy (Whisper): download model ({ModelManager.WhisperModelSize})",
            _ => "High accuracy (Whisper)",
        };
        var item = Item(text, ToggleWhisper, isChecked: settings.UseWhisper && whisperState is WhisperState.Ready or WhisperState.Loading);
        item.ToolTipText = "Whisper re-transcribes what was said: much better on videos and fast speech, adds punctuation.";
        return item;
    }

    Forms.ToolStripMenuItem WhisperOptionsMenu()
    {
        var installed = ModelManager.ListWhisperModels();
        bool gpu = WhisperRuntime.OnGpu;

        var device = Sub($"Run on (now: {WhisperRuntime.LoadedName})",
            Item("Automatic: GPU if available, else CPU", () => SetWhisperDevice(ComputeDevice.Auto), settings.WhisperDevice == ComputeDevice.Auto),
            Item("GPU (Vulkan)", () => SetWhisperDevice(ComputeDevice.Gpu), settings.WhisperDevice == ComputeDevice.Gpu),
            Item("CPU", () => SetWhisperDevice(ComputeDevice.Cpu), settings.WhisperDevice == ComputeDevice.Cpu));
        device.ToolTipText = "Changing this restarts Live Captions.";

        var model = new Forms.ToolStripMenuItem("Model for finished sentences");
        model.DropDownItems.Add(Item("Automatic (largest on GPU, small.en on CPU)", () => SetWhisperModel(null), settings.WhisperModel == null));
        foreach (var path in installed)
        {
            var file = Path.GetFileName(path);
            model.DropDownItems.Add(Item(ModelManager.WhisperDisplayName(path), () => SetWhisperModel(file),
                string.Equals(settings.WhisperModel, file, StringComparison.OrdinalIgnoreCase)));
        }

        var options = Sub("Whisper options", device, model,
            Item(gpu ? "Live updates while speaking" : "Live updates while speaking (needs GPU)", ToggleLiveWhisper,
                isChecked: settings.LiveWhisper && gpu, enabled: gpu));
        var missing = ModelManager.WhisperDownloads.Where(d => !installed.Any(p => Path.GetFileName(p).Equals(d.File, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count > 0)
        {
            options.DropDownItems.Add(new Forms.ToolStripSeparator());
            foreach (var download in missing)
                options.DropDownItems.Add(Item($"Download {download.Label}, {download.Size}", () => StartWhisperDownload(download),
                    enabled: whisperState != WhisperState.Downloading));
        }
        options.DropDownItems.Add(new Forms.ToolStripSeparator());
        options.DropDownItems.Add(Item("Open Whisper models folder", () => OpenFolder(ModelManager.WhisperDir)));
        return options;
    }

    Forms.ToolStripMenuItem Item(string text, Action onClick, bool isChecked = false, bool enabled = true, string? shortcut = null)
    {
        var item = new Forms.ToolStripMenuItem(text) { Checked = isChecked, Enabled = enabled, ShortcutKeyDisplayString = shortcut };
        // Run after the menu has closed.
        item.Click += (_, _) => Dispatcher.InvokeAsync(onClick);
        return item;
    }

    static Forms.ToolStripMenuItem Sub(string text, params Forms.ToolStripItem[] children)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.DropDownItems.AddRange(children);
        return item;
    }

    // ---------- settings actions ----------

    void Change(Action apply)
    {
        apply();
        settings.Normalize();
        window?.ApplyAppearance();
        RequestSave();
    }

    void SetPosition(CaptionPosition position) => Change(() => settings.Position = position);

    void ToggleMicrophone()
    {
        settings.IncludeMicrophone = !settings.IncludeMicrophone;
        if (captionsOn) _ = audio.SetIncludeMicrophoneAsync(settings.IncludeMicrophone);
        RequestSave();
    }

    static bool SafeStartupEnabled()
    {
        try
        {
            return StartupRegistration.IsEnabled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static void ToggleStartup()
    {
        try
        {
            StartupRegistration.Set(!StartupRegistration.IsEnabled);
        }
        catch (Exception ex)
        {
            Log.Error("Changing start-with-Windows", ex);
        }
    }

    void SwitchModel(string path)
    {
        window?.ClearCaptions();
        _ = LoadModelAsync(path);
    }

    static void OpenModelsFolder() => OpenFolder(ModelManager.ModelsDir);

    static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    void RequestSave()
    {
        if (saveTimer == null) return;
        saveTimer.Stop();
        saveTimer.Start();
    }

    void ExitApp()
    {
        if (exiting) return;
        exiting = true;
        downloadCts?.Cancel();
        try
        {
            audio.StopAsync().Wait(3000);
        }
        catch (Exception ex)
        {
            Log.Error("Stopping audio", ex);
        }
        window?.CloseForExit();
        if (tray != null)
        {
            tray.Visible = false;
            tray.Dispose();
            tray = null;
        }
        engine?.Dispose();
        whisperCts?.Cancel();
        refiner?.Dispose();
        FlushAwaitingRefinement();
        saveTimer?.Stop();
        SettingsStore.Save(settings);
        try
        {
            instanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // not owned by this thread
        }
        instanceMutex?.Dispose();
        instanceMutex = null;
        Log.Info("Exited");
        Shutdown();
    }

    /// <summary>Starts a fresh copy (after this one has released the single-instance lock) and exits.</summary>
    void RestartApp()
    {
        var exe = Environment.ProcessPath;
        ExitApp();
        if (exe != null) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
    }
}
