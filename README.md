# Live Captions for Windows 10

Windows 11 has a built-in Live Captions feature (Win + Ctrl + L) that captions whatever your PC is playing. Windows 10 doesn't. This app adds it.

![The caption bar docked at the top of the screen](docs/screenshot.png)

It listens to your speakers (and optionally your microphone), runs speech recognition entirely on your PC, and shows the text in a bar docked to the top or bottom of the screen, or in a floating window. It works with YouTube, Zoom/Teams/Discord calls, podcasts, games, and anything else that makes sound.

## Features

- **Captions any audio the PC plays**, using WASAPI loopback. Follows you when you switch speakers or headphones.
- **Offline.** No account, no cloud. The network is only used once, to download the speech models.
- **Two-stage recognition.** [Vosk](https://alphacephei.com/vosk/) shows words instantly as they're spoken. When a sentence ends, [Whisper](https://github.com/openai/whisper) (small.en, via [Whisper.net](https://github.com/sandrohanea/whisper.net)) re-transcribes it and replaces the draft about a second later, with punctuation, casing and numbers ("$50", not "fifty dollars"). Whisper is much better on real-world audio: music under speech, accents, fast talkers.
- **Docked like the taskbar.** In Top or Bottom position the bar reserves its strip of the screen, so maximized windows don't hide behind it. It always stays on top.
- **Win + Ctrl + L** toggles captions, the same shortcut as Windows 11. A tray icon does too.
- Caption styles (white on black, translucent, yellow on blue, black on white, green monospace), four text sizes, 1–5 lines, scroll back with the mouse wheel.
- Profanity filter, optional microphone mixing, transcripts saved to text files, start with Windows.
- **Portable.** Settings, logs, models and transcripts live in a `data` folder next to the exe.

## Requirements

- Windows 10, 64-bit (Windows 11 works too)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64), unless you build with `-SelfContained`
- About 390 MB of disk for the two models; about 700 MB of RAM in use with both loaded
- A 4+ core CPU is recommended for Whisper. On a Ryzen 5 5600G each sentence takes 0.5–2 s to refine, and captioning uses about one core while speech is playing. With Whisper off, it's about a third of a core. Both drop to near zero when nothing is playing.

## Build and run

```powershell
git clone https://github.com/a-rabin/LiveCaptions10.git
cd LiveCaptions10
.\build.ps1 -Run
```

`build.ps1` publishes to `dist\`. Options:

| Option | What it does |
| --- | --- |
| `-Run` | Starts the app after building |
| `-InstallSdk` | Installs a private .NET 8 SDK into `.tools\dotnet` first (no admin rights, nothing system-wide) |
| `-SelfContained` | Bundles the .NET runtime so the target PC doesn't need it installed |

The build keeps the SDK, NuGet packages, caches and temp files inside `.tools\`, so nothing gets written to your system drive.

On first start the bar offers to download the English speech files (Vosk, 128 MB). Turn on **High accuracy (Whisper)** in the settings menu to download the Whisper model (181 MB). Both go into `dist\data\`.

## Using it

- **Win + Ctrl + L**, a left-click on the tray icon, or the ✕ on the bar turns captions on and off.
- The **⚙ button** on the bar (or right-clicking the tray icon) opens the settings menu: position, style, text size, lines, Whisper, microphone, profanity filter, transcripts, language, start with Windows.
- In **Floating** mode, drag the bar anywhere and resize it from its edges.
- Scroll up on the bar to reread earlier captions. It resumes following when you scroll back to the end.

### Other languages

Download a model for your language from [alphacephei.com/vosk/models](https://alphacephei.com/vosk/models), unzip it into `data\models\`, and pick it under **Language**. The Whisper pass is English-only (`small.en`). Turn it off for other languages.

### Command line

| Argument | |
| --- | --- |
| `--background` | Start in the tray with captions off |
| `--exit` | Close the running copy |
| `--transcript <file>` | Append every finished sentence to a file |
| `--selftest <in.wav> <out.txt>` | Run a WAV through the recognizers and write the text and timings |
| `--dump-audio <file.wav>` | Record exactly what the recognizer hears (for debugging) |

Set `LIVECAPTIONS10_DATA` to use a different data folder.

## How it works

```
speakers (WASAPI loopback) ──┐
microphone (optional) ───────┴─► 16 kHz mono ─► 100 ms jitter buffer ─► mixer thread
                                                                            │
                                                                       30 s queue
                                                                            │
                                                                            ▼
                                             Vosk (recognizer thread): words appear as they're spoken
                                                                            │  sentence ends
                                                                            ▼
                                             Whisper (worker thread): re-transcribes that sentence
                                                                            │
                                                                            ▼
                                             caption bar (docked app bar) + optional transcript file
```

- Audio is converted to 16 kHz mono as each device packet arrives and held in a 100 ms jitter buffer. A real-time mixer thread drains it and synthesizes silence when nothing is playing (loopback sends no packets then).
- The recognizer runs on its own thread behind a 30 s queue, so Vosk's bursty CPU use never makes the audio drop.
- Vosk's word timestamps cut each sentence's audio exactly, so a sentence's first word isn't lost into the previous one. If there's no pause for 15 s (talking over music), a sentence break is forced.
- Whisper normally encodes a fixed 30 s window. The app shrinks the encoder context to the sentence's length, which makes short sentences 3–7× faster with the same output. Known Whisper inventions on silence and music ("Thanks for watching!", repetition loops, "[Music]") are filtered out.
- When nothing has been playing for 2 s, the recognizer stops running entirely.

## Testing

```powershell
# Speaks two sentences through your speakers with the Windows voice and checks the whole pipeline:
# docking, CPU use, transcript, and gaps in the audio the recognizer heard.
powershell -ExecutionPolicy Bypass -File tools\live-test.ps1
```

## Limitations

- Audio played in WASAPI *exclusive* mode (some games and pro-audio apps) can't be captured by loopback.
- The instant (Vosk) text is lowercase until Whisper's version replaces it, and there's no speaker labelling.
- Whisper is CPU-only here. On older dual-core machines, turn Whisper off.

## Credits and licenses

This project is MIT licensed (see [LICENSE](LICENSE)). It uses:

- [NAudio](https://github.com/naudio/NAudio) (MIT): audio capture and resampling
- [Vosk](https://github.com/alphacep/vosk-api) (Apache 2.0) and its [English model](https://alphacephei.com/vosk/models) (Apache 2.0)
- [Whisper.net](https://github.com/sandrohanea/whisper.net) (MIT), wrapping [whisper.cpp](https://github.com/ggerganov/whisper.cpp) (MIT), with OpenAI's [Whisper](https://github.com/openai/whisper) model weights (MIT)

The models are downloaded at runtime and aren't part of this repository.
