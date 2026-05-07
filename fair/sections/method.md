# How it works

Conplaya is a thin orchestration layer on top of Windows Media Foundation, with a pluggable visualization pipeline that consumes the same audio buffer the player decodes.

## Project layout

```
Playback/
 ├─ ConsoleAudioPlayer.cs        # core playback host with visualization hooks
 ├─ Playlist.cs                  # directory scanning + playlist logic
 └─ Visualization/
    ├─ FireEqualizerVisualizer.cs
    ├─ GraphicEqualizerVisualizer.cs
    ├─ AlbumArtRenderer.cs
    ├─ Fft.cs
    └─ IAudioVisualizer.cs
Program.cs                       # entry point + UI orchestration
Terminal/TerminalCapabilities.cs # enables VT sequences on Windows
```

## Visualization pipeline

Every visualizer implements `IAudioVisualizer`. The host hands each one a window of recent audio samples per frame; visualizers return a frame of cells (foreground / background colour pairs) which the host writes to the terminal using ANSI `38;2;r;g;b` / `48;2;r;g;b` true-colour escapes and the `▀` (upper half-block) character. That single character renders two pixels per cell, doubling the effective vertical resolution.

The FFT lives in `Fft.cs` — a straightforward Cooley-Tukey radix-2 implementation sized to the current terminal width. The graphic EQ bins those magnitudes into N columns; the fire visualizer reuses the same magnitudes as a "heat" source for a doom-style propagation kernel.

## Album art

Embedded artwork is decoded from the file's metadata and resampled into a (terminal-width / 2) × (terminal-height) grid. Each cell pairs a top-half pixel (foreground) with a bottom-half pixel (background) and prints `▀`, giving 24-bit colour at full character density. When a track has no embedded art, Conplaya generates a procedural radial gradient from the file's hash so the slot is never empty.

## Terminal capabilities

`TerminalCapabilities.cs` enables `ENABLE_VIRTUAL_TERMINAL_PROCESSING` on Windows so true-colour escapes, alternate screen buffers, and cursor positioning all work in `cmd.exe` and Windows Terminal. The fallback path strips colour and uses a coarser ASCII bar set if the host doesn't support VT.
