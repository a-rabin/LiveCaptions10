# End-to-end test: starts the app, speaks two sentences through the default speakers with the Windows
# voice, then reports docking, CPU, the transcript, and any holes in the audio the recognizer heard.
# Usage (repo root): powershell -ExecutionPolicy Bypass -File tools\live-test.ps1 [-Tag name]
param([string]$Exe = (Join-Path $PSScriptRoot "..\dist\LiveCaptions10.exe"), [string]$Tag = "run")
$sp = Join-Path $PSScriptRoot "..\.tools\tmp"
New-Item -ItemType Directory -Force $sp | Out-Null
Add-Type -AssemblyName System.Drawing, System.Speech
Add-Type @"
using System; using System.IO; using System.Runtime.InteropServices; using System.Collections.Generic;
public static class W2 {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool SystemParametersInfo(int a, int b, ref RECT r, int c);
  public static string WorkArea() { var r = new RECT(); SystemParametersInfo(0x30, 0, ref r, 0); return r.L + "," + r.T + " " + (r.R - r.L) + "x" + (r.B - r.T); }
  public static int[] FirstVisible(int pid) { int[] res = null; EnumWindows((h, p) => { uint id; GetWindowThreadProcessId(h, out id); RECT r; if (res == null && id == pid && IsWindowVisible(h) && GetWindowRect(h, out r) && r.R - r.L > 100) res = new[] { r.L, r.T, r.R - r.L, r.B - r.T }; return true; }, IntPtr.Zero); return res; }
  public static string Gaps(string wav, int minMs) {
    var b = File.ReadAllBytes(wav); int n = (b.Length - 44) / 2; var s = new short[n]; Buffer.BlockCopy(b, 44, s, 0, n * 2);
    var outp = new List<string>(); int i = 0; int min = 16 * minMs;
    while (i < n) { if (s[i] != 0) { i++; continue; } int j = i; while (j < n && s[j] == 0) j++;
      if (j - i >= min && Loud(s, Math.Max(0, i - 4800), i) && Loud(s, j, Math.Min(n, j + 4800))) outp.Add(string.Format("{0:F2}s gap {1}ms", i / 16000.0, (j - i) / 16));
      i = j; }
    return string.Format("{0:F1}s audio; mid-speech gaps: {1}", n / 16000.0, outp.Count == 0 ? "none" : string.Join(", ", outp));
  }
  static bool Loud(short[] s, int a, int e) { for (int k = a; k < e; k++) if (Math.Abs((int)s[k]) > 300) return true; return false; }
}
"@
$transcript = Join-Path $sp "transcript-$Tag.txt"; $dumpWav = Join-Path $sp "heard-$Tag.wav"
Remove-Item $transcript, $dumpWav -ErrorAction SilentlyContinue
"work area before: " + [W2]::WorkArea()
$p = Start-Process $Exe -ArgumentList "--transcript `"$transcript`" --dump-audio `"$dumpWav`"" -PassThru
Start-Sleep -Seconds 5
"work area docked: " + [W2]::WorkArea()
$cpu0 = (Get-Process -Id $p.Id).TotalProcessorTime; $t0 = Get-Date
$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; $s.Volume = 60
$s.Speak("Good evening, and welcome to the news. Tonight, scientists in Canada announced a new way to recycle plastic bottles.")
Start-Sleep -Milliseconds 800
$s.Speak("Do you think this will help the environment? Experts say it could reduce waste by half by next September.")
Start-Sleep -Seconds 3   # let the last sentence finish and its Whisper pass replace the draft
$r = [W2]::FirstVisible($p.Id)
if ($r) { $bmp = New-Object System.Drawing.Bitmap $r[2], $r[3]; $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($r[0], $r[1], 0, 0, $bmp.Size); $g.Dispose(); $bmp.Save((Join-Path $sp "bar-$Tag.png")); $bmp.Dispose(); "bar rect: $($r -join ',')" }
$proc = Get-Process -Id $p.Id
"cpu while speaking: " + [math]::Round(($proc.TotalProcessorTime - $cpu0).TotalSeconds, 1) + "s over " + [math]::Round(((Get-Date) - $t0).TotalSeconds, 1) + "s"
Start-Process $Exe -ArgumentList "--exit" -Wait; Start-Sleep -Seconds 2
"work area after exit: " + [W2]::WorkArea()
"--- transcript"; Get-Content $transcript
"--- log"; Get-Content (Join-Path (Split-Path $Exe) "data\app.log") -Tail 8
"--- audio check"; [W2]::Gaps($dumpWav, 10)
