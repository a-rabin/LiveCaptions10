# Live Captions for Windows 10

Windows 11 has a built-in Live Captions feature (Win + Ctrl + L) that captions whatever your PC is playing. Windows 10 doesn't. This app adds it.

**[Website](https://a-rabin.github.io/LiveCaptions10/)** · **[Download the latest release](https://github.com/a-rabin/LiveCaptions10/releases/latest)**

![The caption bar docked at the top of the screen](docs/screenshot.png)

It listens to your speakers (and optionally your microphone), runs speech recognition entirely on your PC, and shows the text in a bar docked to the top or bottom of the screen, or in a floating window. It works with YouTube, Zoom/Teams/Discord calls, podcasts, games, and anything else that makes sound.

## Features

- **Captions any audio the PC plays**, using WASAPI loopback. Follows you when you switch speakers or headphones.
- **Offline.** No account, no cloud. The network is only used once, to download the speech models.
- **Three layers of recognition**, so text is both instant and accurate:
  1. [Vosk](https://alphacephei.com/vosk/) shows each word the moment it's spoken (rough, lowercase).
  2. While the sentence is still going, [Whisper](https://github.com/openai/whisper) small.en re-transcribes it every half second. Its text replaces Vosk's about a second behind the speaker, and Vosk's newest few words stay visible after it, dimmed.
  3. When the sentence ends, Whisper large-v3-turbo transcribes it once more, with punctuation, casing and numbers ("$50", not "fifty dollars"). This is the most accurate version, and it handles fast talkers, accents and music under speech.
- **Runs Whisper on the GPU** through Vulkan, including AMD and Intel integrated graphics, with automatic fallback to the CPU. Switch between them in the menu.
- **Docked like the taskbar.** In Top or Bottom position the bar reserves its strip of the screen, so maximized windows don't hide behind it. It always stays on top.
- **Win + Ctrl + L** toggles captions, the same shortcut as Windows 11. A tray icon does too.
- Caption styles (white on black, translucent, yellow on blue, black on white, green monospace), four text sizes, 1–5 lines, scroll back with the mouse wheel.
- Profanity filter, optional microphone mixing, transcripts saved to text files, start with Windows.
- **Portable.** Settings, logs, models and transcripts live in a `data` folder next to the exe.

## Requirements

- Windows 10, 64-bit (Windows 11 works too)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64), unless you build with `-SelfContained`
- For the full setup, a GPU with Vulkan support. Almost any from the last ~8 years qualifies, including Radeon/Intel integrated graphics; keep its driver up to date. Without one, Whisper runs on the CPU with small.en only and no live layer.

Measured on a Ryzen 5 5600G using its integrated Radeon graphics:

| | GPU (Vulkan) | CPU only |
| --- | --- | --- |
| Live pass (small.en) | 0.3–0.5 s, max 0.8 s | off |
| Finished sentence | 0.6–1.9 s (large-v3-turbo) | 0.6–2 s (small.en) |
| CPU use while speech plays | about half a core | about one core |
| Memory | 1.3 GB | 0.7 GB |
| Models on disk | 930 MB | 390 MB |

CPU use drops to near zero when nothing is playing.

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

On first start the bar offers to download the English speech files (Vosk, 128 MB). Turn on **High accuracy (Whisper)** in the settings menu to download small.en (181 MB). If you have a GPU, also download large-v3-turbo (547 MB) from **Whisper options**. Everything goes into `dist\data\`.

## Using it

- **Win + Ctrl + L**, a left-click on the tray icon, or the ✕ on the bar turns captions on and off.
- The **⚙ button** on the bar (or right-clicking the tray icon) opens the settings menu: position, style, text size, lines, Whisper, microphone, profanity filter, transcripts, language, start with Windows.
- **Whisper options** controls what runs Whisper (Automatic / GPU / CPU; changing it restarts the app), which model finishes sentences, live updates while speaking, and model downloads.
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

Set `LIVECAPTIONS10_DATA` to use a different data folder. For benchmarking with `--selftest`, `LC10_WHISPER_DEVICE` (`cpu`/`gpu`) and `LC10_WHISPER_MODEL` (a file name in `data\whisper`) override the automatic choices.

## Streaming engines (prototype)

The `prototype/sherpa-onnx-streaming` branch adds streaming transducer models via [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx), the same family of model Windows 11 Live Captions uses. They process each slice of audio once and emit words as they're heard, instead of re-reading whole clips like Whisper. Pick one under ⚙ → **Speech engine**.

Word error rate on synthetic test clips (Windows voices, lower is better), plus CPU time per second of audio on a Ryzen 5 5600G:

| Engine | Clean | Fast speech (2 voices) | Fast + music bed | CPU per second |
| --- | --- | --- | --- | --- |
| Vosk lgraph (original) | 6.5–9.7% | 6.9–16.7% | 37.5% | 0.68–1.57 s |
| NeMo streaming FastConformer 480 ms | 3.2% | 16.7–18.1% | 23.6% | 0.10 s |
| **Kroko streaming Zipformer** | **0.0%** | **1.4–4.2%** | **4.2%** | **0.06 s** |

Delay is the trade-off. A streaming model decodes in fixed chunks set when it's exported. Kroko's are 1.28 s, so its words appear about 1.3 s after they're spoken, in blocks; NeMo updates every 0.56 s and Vosk every 0.3–0.4 s. Kroko's larger Community "L" packages ([huggingface.co/Banafo/Kroko-ASR](https://huggingface.co/Banafo/Kroko-ASR), unpacked automatically from `data\sherpa\*.data`) use the same 1.28 s chunks ("64-L") or 2.56 s ("128-L"), so none is faster. To keep the blocks from feeling jerky, ⚙ → **Text appears** types new text out letter by letter. *Smooth* spreads each block evenly until the next one arrives, *Quick* types it in about a third of that gap, and *All at once* turns the effect off.

Kroko also writes its own punctuation and capitalization. With Kroko, a second pass added little. Parakeet TDT 0.6B (CPU, about 1 s per sentence) and Whisper turbo (GPU, about 3 s) each fixed some words and broke others, and Whisper sometimes repeated a sentence. So Kroko alone is the recommended setup: about 0.16 of a CPU core for the whole app while speech plays, no GPU, 300 MB of RAM.

Prototype setup: unpack these into `data\sherpa\` (there's no in-app download for them yet):

- [`sherpa-onnx-streaming-zipformer-en-kroko-2025-08-06`](https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-streaming-zipformer-en-kroko-2025-08-06.tar.bz2) (54 MB, [CC-BY-SA](https://huggingface.co/Banafo/Kroko-ASR))
- optional: [`sherpa-onnx-nemo-streaming-fast-conformer-transducer-en-480ms-int8`](https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-streaming-fast-conformer-transducer-en-480ms-int8.tar.bz2) (101 MB, CC-BY-4.0), which needs [`sherpa-onnx-online-punct-en-2024-08-06`](https://github.com/k2-fsa/sherpa-onnx/releases/download/punctuation-models/sherpa-onnx-online-punct-en-2024-08-06.tar.bz2) (29 MB) for punctuation
- optional second pass: [`sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8`](https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8.tar.bz2) (460 MB, CC-BY-4.0)

`--selftest` takes `LC10_ENGINE` (`vosk` / `nemo` / `kroko`) and `LC10_SECOND` (`whisper` / `parakeet` / `none`) for benchmarking.

## How it works

```
speakers (WASAPI loopback) ──┐
microphone (optional) ───────┴─► 16 kHz mono ─► 100 ms jitter buffer ─► mixer thread
                                                                            │
                                                                       30 s queue
                                                                            │
                                                                            ▼
                                     Vosk (recognizer thread): words appear as they're spoken
                                          │                                 │
                     every 0.5 s, the settled part                    sentence ends
                     of the sentence so far                                 │
                                          ▼                                 ▼
                     Whisper small.en (GPU): live text          Whisper large-v3-turbo (GPU): final text
                                          │                                 │
                                          └──────────────┬──────────────────┘
                                                         ▼
                                     caption bar (docked app bar) + optional transcript file
```

- Audio is converted to 16 kHz mono as each device packet arrives and held in a 100 ms jitter buffer. A real-time mixer thread drains it and synthesizes silence when nothing is playing (loopback sends no packets then).
- The recognizer runs on its own thread behind a 30 s queue, so Vosk's bursty CPU use never makes the audio drop.
- Vosk's word timestamps cut each sentence's audio exactly, so a sentence's first word isn't lost into the previous one. If there's no pause for 15 s (talking over music), a sentence break is forced.
- Whisper normally encodes a fixed 30 s window. The app shrinks the encoder context to fit the clip (in 0.5 s steps), which makes short sentences 3–7× faster. The fit has to be tight, because Whisper fills leftover silence with repeats of the clip's opening.
- Whisper's temperature-fallback retries are off, and output length is capped at about 10 tokens per second of audio. On short, garbled clips the retries caused 20–30 s stalls. Known inventions ("Thanks for watching!", repetition loops, "[Music]", a repeated opening sentence) are filtered out.
- Live passes stop at the end of Vosk's second-to-last word, because the last one is still changing. The dimmed tail starts after the Vosk word matching Whisper's last word.
- Whisper uses (physical cores − 2) CPU threads, because its threads spin while waiting and would otherwise starve the instant recognizer.
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
- Singing is hard for every model here, and so is very loud music under speech.
- Without a GPU, only the finished-sentence pass runs, and on older dual-core machines Whisper is best turned off.

## Credits and licenses

This project is MIT licensed (see [LICENSE](LICENSE)). It uses:

- [NAudio](https://github.com/naudio/NAudio) (MIT): audio capture and resampling
- [Vosk](https://github.com/alphacep/vosk-api) (Apache 2.0) and its [English model](https://alphacephei.com/vosk/models) (Apache 2.0)
- [Whisper.net](https://github.com/sandrohanea/whisper.net) (MIT), wrapping [whisper.cpp](https://github.com/ggerganov/whisper.cpp) (MIT), with OpenAI's [Whisper](https://github.com/openai/whisper) model weights (MIT)

The models are downloaded at runtime and aren't part of this repository.
