# What it does

A single self-contained `play.exe` (no .NET runtime install required) that turns any terminal window into a full audio player + visualizer.

## Controls

| Key            | Action                                       |
| -------------- | -------------------------------------------- |
| `Left Arrow`   | Rewind 5 seconds                             |
| `Right Arrow`  | Fast-forward 5 seconds                       |
| `Space`        | Pause / resume playback                      |
| `Up Arrow`     | Previous track (ignored if only one track)   |
| `Down Arrow`   | Next track (ignored if only one track)       |
| `Ctrl + C`     | Stop playback gracefully                     |
| `V`            | Cycle visualization styles                   |

The status line switches between **Now playing** and **Paused** so the current state is always visible. Track metadata (title, artist, album) is shown when present and falls back to the file name without ever spilling past the playback bar.

## Visualizer modes

| Mode         | What you see                                                        |
| ------------ | ------------------------------------------------------------------- |
| Fire EQ      | Doom-style fire propagation seeded by FFT magnitudes per column.    |
| Graphic EQ   | Classic full-width bar graph, gradient-coloured by frequency band.  |
| Waveform     | Mirrored stereo waveform centred on the playback line.              |
| Pixel pulse  | Sparse pulsing point field driven by per-band peaks.                |

## Releases

Continuous deployment runs on every push to `master`:

1. **GitVersion** stamps a SemVer (e.g. `0.6.3`) from the commit graph — no manual tagging.
2. `dotnet publish` builds a self-contained, single-file `win-x64` binary (trimming disabled to keep Media Foundation interop intact).
3. The release workflow zips it, tags the commit, and creates the GitHub Release with the archive attached.
4. A separate **Chocolatey Publish** workflow packs and pushes a `choco` package; `choco install conplaya` then puts `play` on your `PATH`.

Verbose logging is available via `-v` / `--verbose` (playlist contents, playback lifecycle, artwork decode fallbacks).

```bash
dotnet run -- -v "C:\music\track01.mp3"
```
