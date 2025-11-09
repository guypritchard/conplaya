# Conplaya 🎧

> **Note:** This project's source code and documentation were produced with the assistance of OpenAI GPT tooling.

Conplaya is a modern .NET 9 console audio player that keeps everything in one terminal window: Unicode album art, a live FFT-based graphic equalizer, timing HUD, and keyboard-driven playback controls (seek, pause, playlist navigation).

> _Tip:_ Run the app in a VT-enabled console (Windows Terminal, VS Code terminal, etc.) to see the 24‑bit colour album art and Unicode bars in all their glory.

## Features

- **Drop-in playlisting** – start with any `.mp3`, `.wav`, `.aac`, `.aiff`, `.flac`, `.wma`, or `.m4a` file and Conplaya will automatically queue every supported track in the same folder.
- **Unicode visualizer** – FFT-powered log-scale graphic equalizer rendered with block glyphs; it stays horizontally aligned with the playback progress bar.
- **Live album art** – embedded artwork (or a procedural gradient placeholder) is rendered beside the EQ using `\u2580` half blocks with foreground/background colour pairs.
- **Keyboard controls** – seek, pause, cancel, or cycle tracks without leaving the terminal.
- **Pluggable visualization pipeline** – the equalizer is one implementation of `IAudioVisualizer`; you can drop in custom visual modules.
- **Smart status text** – shows “Now playing”/“Paused” with track metadata (title, artist, album) when present, falling back to file name without ever spilling past the playback bar.

## Quickstart

```bash
dotnet build
dotnet run -- "C:\music\track01.mp3"
```

After publishing (`dotnet publish` or the GitHub release workflow), you’ll get a single-file binary named `play.exe` that can run without an installed .NET runtime.

## Chocolatey Package

The `chocolatey/` folder contains a nuspec and install scripts for packaging Conplaya:

```bash
cd chocolatey
choco pack --version <MajorMinorPatch>
choco push conplaya.<version>.nupkg --source https://push.chocolatey.org/
```

The install script downloads the `conplaya-<version>.zip` asset from GitHub Releases and shims it as `play`. The release workflow automatically packs the Chocolatey package (the version comes from GitVersion’s `MajorMinorPatch`). To push it to Chocolatey.org, run the separate **"Chocolatey Publish"** workflow in GitHub Actions (see below) once you’re ready and supply your `CHOCO_API_KEY` secret.

### Controls

| Key              | Action                                       |
| ---------------- | -------------------------------------------- |
| `Left Arrow`     | Rewind 5 seconds                             |
| `Right Arrow`    | Fast-forward 5 seconds                       |
| `Space`          | Pause / resume playback                      |
| `Up Arrow`       | Previous track (ignored if only one track)   |
| `Down Arrow`     | Next track (ignored if only one track)       |
| `Ctrl + C`       | Stop playback gracefully                     |

The status line automatically switches between **Now playing** and **Paused** so you always know the current state.

### Verbose Logging

Append `-v` or `--verbose` to any command to get detailed diagnostics (playlist contents, playback lifecycle, artwork fallbacks). Example:

```bash
dotnet run -- -v "C:\music\track01.mp3"
```

## Project Structure

```
Playback/
 ├─ ConsoleAudioPlayer.cs        # Core playback host with visualization hooks
 ├─ Playlist.cs                  # Directory scanning + playlist logic
 └─ Visualization/
    ├─ GraphicEqualizerVisualizer.cs
    ├─ AlbumArtRenderer.cs
    ├─ Fft.cs
    └─ IAudioVisualizer.cs
Program.cs                      # Entry point + UI orchestration
Terminal/TerminalCapabilities.cs# Enables VT sequences on Windows
```

## GitVersion & Releases

The repository uses [GitVersion](https://gitversion.net) in **ContinuousDeployment** mode (`GitVersion.yml`). Every commit to `master` automatically bumps the version (based on `+semver:` hints or the default scheme) and that SemVer is injected into builds and releases—no manual tagging needed.

### Release (`.github/workflows/release.yml`)

- Runs automatically on every push to `master`.
- Uses GitVersion to stamp a new semantic version (e.g. `0.6.3`), produces a self-contained single-file `win-x64` executable via `dotnet publish` (trimming disabled to keep Media Foundation support), zips it, and then relies on the built-in `actions/create-release` + `actions/upload-release-asset` steps to tag and publish the release with the archive attached. If a release/tag with that version already exists, it is deleted automatically before the new one is created. No external .NET runtime is required for the released binary.

## Development Checklist

1. `dotnet restore`
2. `dotnet run -- "<path-to-audio-file>"` to verify end-to-end behavior.
3. Keep `bin/` and `obj/` out of source control (already enforced via `.gitignore`).
4. Let GitVersion manage versions—avoid hardcoding `AssemblyVersion`.

## Roadmap Ideas

- Additional visualizers (waveform, waterfall)
- Configurable FFT sizes and colour palettes
- Cross-platform media transport controls (media keys)
- Optional HTTP or IPC remote control

PRs welcome! Feel free to open issues with feature requests or bug reports.
