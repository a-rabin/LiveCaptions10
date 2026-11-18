using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace LiveCaptions10;

/// <summary>
/// The caption bar. Docked (top/bottom, reserving screen space) or floating (movable, resizable).
/// Shows finished sentences followed by the sentence in progress, scrolled to the newest line.
/// </summary>
internal partial class CaptionWindow : Window
{
    const int HotkeyId = 0x4C43;
    const double ParagraphGapSeconds = 3.0;
    const int MaxSegments = 80;

    readonly AppSettings settings;
    readonly AppBar appBar;
    readonly List<Segment> segments = new();
    IntPtr hwnd;
    PartialUpdate? voskPartial;
    LiveText? live;
    long lastFinishedUtterance;
    Brush tailBrush = Brushes.Gray;
    bool partialBreak, utteranceOpen, allowClose, placing, autoScroll = true;
    DateTime lastFinalUtc = DateTime.MinValue;
    long lastSegmentId;

    public CaptionWindow(AppSettings settings)
    {
        this.settings = settings;
        InitializeComponent();
        appBar = new AppBar(this);
        new WindowInteropHelper(this).EnsureHandle(); // so the hotkey works before the window is first shown

        Scroller.ScrollChanged += OnScrollChanged;
        Root.MouseLeftButtonDown += OnRootMouseDown;
        LocationChanged += (_, _) => RememberFloatingBounds();
        SizeChanged += (_, _) => RememberFloatingBounds();
        DpiChanged += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (appBar.IsDocked) appBar.Reposition();
        });

        ApplyAppearance();
        Render();
    }

    public event Action? ToggleRequested;
    public event Action? CloseRequested;
    public event Action? ActionRequested;
    public event Action? LayoutChanged;
    public event Action<int, int>? SettingsMenuRequested;

    public bool HotkeyRegistered { get; private set; }

    double LineHeight => Math.Ceiling(settings.FontSize * 1.3);

    // lines + text margins (8) + grid margins (12) + border (1); never smaller than the icon buttons
    double BarHeight => Math.Max(settings.Lines * LineHeight, 34) + 8 + 12 + 1;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        hwnd = new WindowInteropHelper(this).Handle;

        // Keep the bar out of Alt+Tab.
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex = (ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));

        var source = HwndSource.FromHwnd(hwnd);
        source.AddHook(WndProc);
        appBar.Attach(hwnd, source);

        // Same shortcut as Windows 11: Win + Ctrl + L
        HotkeyRegistered = Native.RegisterHotKey(hwnd, HotkeyId, Native.MOD_WIN | Native.MOD_CONTROL | Native.MOD_NOREPEAT, 0x4C);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!allowClose)
        {
            e.Cancel = true; // Alt+F4 turns captions off instead of quitting
            CloseRequested?.Invoke();
        }
        base.OnClosing(e);
    }

    // ---------- showing / placement ----------

    public void ShowCaptions()
    {
        if (!IsVisible)
        {
            placing = true;
            Show();
            placing = false;
        }
        ApplyPlacement();
    }

    public void HideCaptions()
    {
        appBar.Undock();
        Hide();
    }

    public void ReleaseAppBar() => appBar.Undock();

    public void CloseForExit()
    {
        allowClose = true;
        if (hwnd != IntPtr.Zero) Native.UnregisterHotKey(hwnd, HotkeyId);
        appBar.Undock();
        Close();
    }

    public void ApplyAppearance()
    {
        var style = CaptionStyles.Get(settings.Style);
        var foreground = new SolidColorBrush(style.Foreground);
        foreground.Freeze();
        var tail = new SolidColorBrush(Color.FromArgb(0x99, style.Foreground.R, style.Foreground.G, style.Foreground.B));
        tail.Freeze();
        tailBrush = tail;

        Root.Background = new SolidColorBrush(style.Background);
        CaptionText.Foreground = StatusText.Foreground = SettingsButton.Foreground = CloseButton.Foreground = foreground;
        CaptionText.FontFamily = new FontFamily(style.FontFamily);
        CaptionText.FontSize = settings.FontSize;
        CaptionText.LineHeight = LineHeight;
        StatusText.FontSize = Math.Max(14, settings.FontSize * 0.75);
        ApplyPlacement();
    }

    void ApplyPlacement()
    {
        if (!IsVisible) return;
        placing = true;
        try
        {
            double height = BarHeight;
            bool floating = settings.Position == CaptionPosition.Floating;

            Root.CornerRadius = new CornerRadius(floating ? 8 : 0);
            Root.BorderThickness = settings.Position switch
            {
                CaptionPosition.Top => new Thickness(0, 0, 0, 1),
                CaptionPosition.Bottom => new Thickness(0, 1, 0, 0),
                _ => new Thickness(1),
            };
            ResizeMode = floating ? ResizeMode.CanResize : ResizeMode.NoResize;
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
                ResizeBorderThickness = new Thickness(floating ? 6 : 0),
            });

            if (floating)
            {
                appBar.Undock();
                MinWidth = 320;
                MinHeight = MaxHeight = height;
                PlaceFloating(height);
            }
            else
            {
                MinWidth = 0;
                MinHeight = 0;
                MaxHeight = double.PositiveInfinity;
                appBar.Dock(settings.Position == CaptionPosition.Top, height);
            }
        }
        finally
        {
            placing = false;
        }
        if (autoScroll) Scroller.ScrollToEnd();
    }

    void PlaceFloating(double height)
    {
        var work = SystemParameters.WorkArea;
        double width = Math.Max(MinWidth, settings.FloatWidth ?? Math.Min(1000, work.Width * 0.6));
        double left = settings.FloatLeft ?? work.Left + (work.Width - width) / 2;
        double top = settings.FloatTop ?? work.Bottom - height - 48;

        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!screen.Contains(new Point(left + 40, top + 10)))
        {
            // Saved spot is off-screen (monitor unplugged?) — fall back to bottom centre.
            left = work.Left + (work.Width - width) / 2;
            top = work.Bottom - height - 48;
        }

        Width = width;
        Height = height;
        Left = left;
        Top = top;
    }

    void RememberFloatingBounds()
    {
        if (placing || !IsVisible || settings.Position != CaptionPosition.Floating) return;
        settings.FloatLeft = Left;
        settings.FloatTop = Top;
        settings.FloatWidth = Width;
        LayoutChanged?.Invoke();
    }

    void OnRootMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (settings.Position != CaptionPosition.Floating || e.ChangedButton != MouseButton.Left) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // mouse already released
        }
    }

    // ---------- captions ----------

    /// <summary>Vosk's words for the sentence in progress (instant, rough).</summary>
    public void ShowPartial(PartialUpdate update)
    {
        if (update.Utterance <= lastFinishedUtterance) return; // arrived after its sentence was finished
        if (update.Text.Length == 0)
        {
            voskPartial = null;
            live = null;
            utteranceOpen = false;
            Render();
            return;
        }
        OpenUtterance();
        voskPartial = update;
        if (live != null && live.Utterance != update.Utterance) live = null;
        Render();
    }

    /// <summary>Whisper's text for the settled part of the sentence in progress (accurate, ~1 s behind).</summary>
    public void ShowLive(LiveSnapshot snapshot, string text)
    {
        if (snapshot.Utterance <= lastFinishedUtterance) return;
        OpenUtterance();
        live = new LiveText(snapshot.Utterance, text, snapshot.CoveredUntil, snapshot.CoveredWords);
        Render();
    }

    /// <summary>Commits a finished utterance; returns its id and the text as displayed ("" if dropped).</summary>
    public (long Id, string Text) ShowFinal(FinalUpdate update)
    {
        string text;
        if (live != null && live.Utterance == update.Utterance)
        {
            // Whisper's live text plus whatever Vosk heard after it; the final Whisper pass follows shortly.
            var after = update.Words.Where(w => w.Start >= live.CoveredUntil - 0.05).Select(w => w.Word);
            var (main, tail) = Compose(live, string.Join(' ', after));
            text = tail.Length > 0 ? main + " " + tail : main;
            if (text.Length > 0 && text[^1] is not ('.' or '?' or '!')) text += ".";
        }
        else
        {
            text = CaptionFormatter.FormatFinal(update.Text, settings.FilterProfanity);
        }
        lastFinishedUtterance = Math.Max(lastFinishedUtterance, update.Utterance);
        voskPartial = null;
        live = null;

        long id = 0;
        if (text.Length > 0)
        {
            id = ++lastSegmentId;
            segments.Add(new Segment(id, text, utteranceOpen ? partialBreak : StartsNewParagraph()));
            if (segments.Count > MaxSegments) segments.RemoveRange(0, segments.Count - MaxSegments);
            lastFinalUtc = DateTime.UtcNow;
        }
        utteranceOpen = false;
        Render();
        return (id, text);
    }

    /// <summary>Swaps a sentence for its refined version; empty text removes it.</summary>
    public void ReplaceSegment(long id, string text)
    {
        int index = segments.FindIndex(s => s.Id == id);
        if (index < 0) return; // already scrolled out or cleared
        if (text.Length == 0) segments.RemoveAt(index);
        else segments[index] = segments[index] with { Text = text };
        Render();
    }

    public void ClearCaptions()
    {
        segments.Clear();
        voskPartial = null;
        live = null;
        utteranceOpen = false;
        autoScroll = true;
        Render();
    }

    public void SetStatus(string text, string? action)
    {
        StatusText.Text = text;
        ActionButton.Content = action;
        ActionButton.Visibility = action == null ? Visibility.Collapsed : Visibility.Visible;
    }

    bool StartsNewParagraph() =>
        segments.Count > 0 && (DateTime.UtcNow - lastFinalUtc).TotalSeconds > ParagraphGapSeconds;

    void OpenUtterance()
    {
        if (utteranceOpen) return;
        utteranceOpen = true;
        partialBreak = StartsNewParagraph();
    }

    /// <summary>The sentence in progress: main text (normal colour) and the newest unconfirmed words (dimmed).</summary>
    (string Main, string Tail) InProgress()
    {
        if (live != null)
        {
            // Vosk's words beyond the ones Whisper has already transcribed.
            var newer = voskPartial?.Utterance == live.Utterance ? WordsAfter(voskPartial.Text, live) : "";
            return Compose(live, newer);
        }
        if (voskPartial != null) return (CaptionFormatter.FormatPartial(voskPartial.Text, settings.FilterProfanity), "");
        return ("", "");
    }

    /// <summary>
    /// Vosk's words that come after Whisper's text. Whisper's last word is looked up among Vosk's words near
    /// where it should be (Vosk often adds or drops a word, so plain counting can be off by one or two).
    /// </summary>
    static string WordsAfter(string voskText, LiveText whisper)
    {
        var vosk = voskText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var whisperWords = whisper.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Normalize).Where(w => w.Length > 0).ToArray();
        int split = Math.Min(whisper.CoveredWords, vosk.Length);
        if (whisperWords.Length > 0)
        {
            var last = whisperWords[^1];
            for (int i = Math.Min(vosk.Length - 1, whisper.CoveredWords + 2); i >= Math.Max(0, whisper.CoveredWords - 4); i--)
            {
                if (SameWord(Normalize(vosk[i]), last))
                {
                    split = i + 1;
                    break;
                }
            }
        }
        return string.Join(' ', vosk.Skip(split));
    }

    static string Normalize(string word) => new string(word.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    // Vosk and Whisper spell some words differently ("fifty" / "50", "news" / "new"): compare loosely.
    static bool SameWord(string a, string b) =>
        a.Length > 0 && b.Length > 0 && (a == b || (a.Length >= 3 && b.Length >= 3 && a[..3] == b[..3]));

    (string Main, string Tail) Compose(LiveText whisper, string newerVoskWords)
    {
        var main = CaptionFormatter.FormatRefined(whisper.Text, settings.FilterProfanity);
        var tail = CaptionFormatter.FormatTail(newerVoskWords, settings.FilterProfanity);
        if (tail.Length > 0) main = main.TrimEnd('.', '…', ',', ' '); // the sentence continues
        return (main, tail);
    }

    void Render()
    {
        var settled = new StringBuilder();
        foreach (var s in segments) Append(settled, s.Text, s.BreakBefore);
        var (main, tail) = InProgress();
        if (main.Length > 0) Append(settled, main, partialBreak);

        CaptionText.Inlines.Clear();
        AddText(settled.ToString(), null);
        if (tail.Length > 0) AddText((settled.Length > 0 ? " " : "") + tail, tailBrush);
        StatusPanel.Visibility = settled.Length + tail.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (autoScroll) Scroller.ScrollToEnd();
    }

    void AddText(string text, Brush? brush)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) CaptionText.Inlines.Add(new LineBreak());
            if (lines[i].Length == 0) continue;
            var run = new Run(lines[i]);
            if (brush != null) run.Foreground = brush;
            CaptionText.Inlines.Add(run);
        }
    }

    static void Append(StringBuilder sb, string text, bool newParagraph)
    {
        if (sb.Length > 0) sb.Append(newParagraph ? "\n" : " ");
        sb.Append(text);
    }

    void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // If the user scrolls back with the mouse wheel, stop following until they return to the end.
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
            autoScroll = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 1;
        else if (autoScroll)
            Scroller.ScrollToVerticalOffset(Scroller.ExtentHeight);
    }

    // ---------- buttons / messages ----------

    void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var p = SettingsButton.PointToScreen(new Point(0, SettingsButton.ActualHeight));
        SettingsMenuRequested?.Invoke((int)p.X, (int)p.Y);
    }

    void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    void ActionButton_Click(object sender, RoutedEventArgs e) => ActionRequested?.Invoke();

    IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            ToggleRequested?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    readonly record struct Segment(long Id, string Text, bool BreakBefore);

    sealed record LiveText(long Utterance, string Text, double CoveredUntil, int CoveredWords);
}
