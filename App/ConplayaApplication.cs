using System.IO;
using System.Linq;
using System.Text;
using Conplaya.Logging;
using Conplaya.Playback;
using Conplaya.Playback.Control;
using Conplaya.Playback.Visualization;

namespace Conplaya.App;

internal sealed class ConplayaApplication
{
    private const int AlbumArtPixels = 18;
    private const int AlbumArtRows = AlbumArtPixels / 2;
    private const int FlameRows = 5;
    private const int AlbumArtWidthChars = AlbumArtPixels;
    private const int MinimumEqColumn = AlbumArtWidthChars + 2;

    private readonly AppOptions _options;
    private int _activeVisualizerIndex;

    public ConplayaApplication(AppOptions options) => _options = options;

    public async Task<int> RunAsync()
    {
        string? filePath = ResolveFilePath();
        if (filePath is null)
        {
            return 1;
        }

        var playlist = Playlist.FromSeed(filePath);
        int currentIndex = Math.Max(playlist.IndexOf(filePath), 0);
        string folderLabel = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? Directory.GetCurrentDirectory();

        Console.WriteLine($"Loaded {playlist.Count} track(s) from '{folderLabel}'.");
        Logger.Verbose($"Playlist entries:{Environment.NewLine}{string.Join(Environment.NewLine, Enumerable.Range(0, playlist.Count).Select(i => $"  [{i + 1}] {playlist[i]}"))}");

        var layout = PrepareLayout();
        var albumArtRenderer = new AlbumArtRenderer(layout.ArtTopRow, 0, AlbumArtPixels, AlbumArtWidthChars);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        bool originalCursorVisible = CaptureCursorVisibility();
        if (OperatingSystem.IsWindows())
        {
            TrySetCursorVisible(false);
        }

        int exitCode = 0;
        string exitMessage = "Playback finished.";

        using var controller = new PlaybackController(TimeSpan.FromSeconds(5));
        controller.SetTrackAdvanceEnabled(playlist.Count > 1);

        bool isPaused = false;
        string currentTrackDisplay = filePath;

        void RenderStatusLine() =>
            UpdateStatusLine(
                layout.StatusRow,
                BuildStatus(currentTrackDisplay, currentIndex, playlist.Count, isPaused),
                layout.MetadataColumnOffset,
                layout.MetadataWidth);

        controller.SetPauseChangedHandler(paused =>
        {
            isPaused = paused;
            RenderStatusLine();
        });

        try
        {
            bool reachedEnd = false;

            while (!cts.IsCancellationRequested && playlist.Count > 0 && !reachedEnd)
            {
                string currentTrack = playlist[currentIndex];
                currentTrackDisplay = currentTrack;
                isPaused = false;

                albumArtRenderer.Render(currentTrack);
                var palette = albumArtRenderer.CurrentPalette;
                RenderStatusLine();
                Logger.Info($"Now playing {currentTrack} [{currentIndex + 1}/{playlist.Count}]");

                using var visualizer = CreateVisualizerSwitcher(layout, _activeVisualizerIndex);
                visualizer.SetPalette(palette);
                controller.SetVisualizationToggleHandler(() =>
                {
                    int newIndex = visualizer.CycleNext();
                    _activeVisualizerIndex = newIndex;
                    Logger.Info($"Visualizer switched to '{visualizer.CurrentLabel}'.");
                });
                await using var player = new ConsoleAudioPlayer(currentTrack, visualizer);

                controller.AttachPlayer(player);

                using var trackCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                TrackAdvance requestedAdvance = TrackAdvance.None;

                controller.SetTrackAdvanceHandler(advance =>
                {
                    if (advance == TrackAdvance.None)
                    {
                        return;
                    }

                    requestedAdvance = advance;
                    trackCts.Cancel();
                });

                try
                {
                    await player.PlayAsync(trackCts.Token);
                }
                catch (OperationCanceledException) when (requestedAdvance != TrackAdvance.None)
                {
                    // track skip requested
                }
                finally
                {
                    controller.SetTrackAdvanceHandler(null);
                    controller.SetVisualizationToggleHandler(null);
                    controller.AttachPlayer(null);
                }

                if (cts.IsCancellationRequested)
                {
                    break;
                }

                if (requestedAdvance == TrackAdvance.Previous)
                {
                    currentIndex = playlist.GetPreviousIndexCircular(currentIndex);
                    continue;
                }

                if (requestedAdvance == TrackAdvance.Next)
                {
                    currentIndex = playlist.GetNextIndexCircular(currentIndex);
                    continue;
                }

                if (currentIndex >= playlist.Count - 1)
                {
                    reachedEnd = true;
                }
                else
                {
                    currentIndex += 1;
                }
            }
        }
        catch (OperationCanceledException)
        {
            exitCode = 0;
            exitMessage = "Playback cancelled.";
            Logger.Warn(exitMessage);
        }
        catch (Exception ex)
        {
            exitCode = 1;
            exitMessage = $"Playback failed: {ex}";
            Logger.Error(exitMessage);
        }
        finally
        {
            MoveCursorPastUi(layout);
            if (OperatingSystem.IsWindows())
            {
                TrySetCursorVisible(originalCursorVisible);
            }
        }

        if (exitCode == 0)
        {
            Console.WriteLine(exitMessage);
        }
        else
        {
            Console.Error.WriteLine(exitMessage);
        }

        return exitCode;
    }

    private string? ResolveFilePath()
    {
        if (string.IsNullOrWhiteSpace(_options.FilePath))
        {
            ReportMissingArguments();
            return null;
        }

        string candidate = _options.FilePath.Trim().Trim('"');
        if (candidate == ".")
        {
            candidate = Directory.GetCurrentDirectory();
        }

        string? resolvedFilePath = null;
        if (Directory.Exists(candidate))
        {
            string directory = Path.GetFullPath(candidate);
            if (!Playlist.TryResolveFirstTrackFromDirectory(directory, out resolvedFilePath) || string.IsNullOrWhiteSpace(resolvedFilePath))
            {
                Logger.Error($"No supported audio files were found in '{directory}'.");
                return null;
            }

            Logger.Info($"Directory specified ('{directory}'); queuing all supported tracks within it.");
        }
        else if (File.Exists(candidate))
        {
            resolvedFilePath = Path.GetFullPath(candidate);
        }
        else
        {
            Logger.Error("Please supply a valid audio file path (.mp3/.wav/.aiff).");
            return null;
        }

        Logger.Info($"Starting Conplaya (verbose={(_options.Verbose ? "on" : "off")})");
        return resolvedFilePath;
    }

    private static ConsoleLayout PrepareLayout()
    {
        int reservedRows = AlbumArtRows;
        int visualTopRow = ReserveVisualizerArea(reservedRows);
        int artTopRow = visualTopRow;
        int infoRow = visualTopRow + reservedRows - 1;
        int metadataColumn = MinimumEqColumn;
        int metadataWidth = Math.Max(20, SafeWindowWidth() - metadataColumn - 2);
        int consoleWidth = SafeWindowWidth();
        metadataWidth = Math.Clamp(metadataWidth, 20, Math.Max(20, consoleWidth - metadataColumn - 1));
        int flameColumn = metadataColumn;
        int statusRow = visualTopRow + FlameRows;
        if (statusRow + 2 >= visualTopRow + reservedRows)
        {
            statusRow = Math.Max(visualTopRow, (visualTopRow + reservedRows) - 3);
        }

        return new ConsoleLayout(
            VisualTopRow: visualTopRow,
            ArtTopRow: artTopRow,
            InfoRow: infoRow,
            StatusRow: statusRow,
            MetadataColumnOffset: metadataColumn,
            MetadataWidth: metadataWidth,
            FlameColumnOffset: flameColumn,
            ReservedRows: reservedRows,
            FlameRows: FlameRows);
    }

    private static string BuildStatus(string path, int index, int total, bool paused)
    {
        var label = TrackMetadata.FromFile(path);
        string trackLabel = !string.IsNullOrWhiteSpace(label.Title)
            ? label.Title
            : Path.GetFileNameWithoutExtension(path);

        if (string.IsNullOrWhiteSpace(trackLabel))
        {
            trackLabel = Path.GetFileName(path);
        }

        string order = $"[{index + 1}/{total}]";
        string prefix = paused ? "[Paused] " : string.Empty;
        string firstLine = $"{order} {prefix}{trackLabel}".Trim();

        var lines = new List<string> { firstLine };

        if (!string.IsNullOrWhiteSpace(label.Artist))
        {
            lines.Add(label.Artist);
        }

        if (!string.IsNullOrWhiteSpace(label.Album))
        {
            lines.Add(label.Album);
        }

        while (lines.Count < 3)
        {
            lines.Add(string.Empty);
        }

        return string.Join(Environment.NewLine, lines.Take(3));
    }

    private static int ReserveVisualizerArea(int rows)
    {
        string blank = new string(' ', Math.Max(40, SafeWindowWidth()));

        for (int i = 0; i < rows; i++)
        {
            Console.WriteLine(blank);
        }

        int endRow = SafeCursorTop();
        int topRow = endRow - rows;
        return Math.Max(0, topRow);
    }

    private static void MoveCursorPastUi(ConsoleLayout layout)
    {
        int artBottom = layout.VisualTopRow + layout.ReservedRows;
        int statusBottom = layout.StatusRow + 3; // status text renders up to 3 lines
        int targetRow = Math.Max(artBottom, statusBottom) + 1;
        int safeRow = Math.Clamp(targetRow, 0, SafeBufferHeight() - 1);
        try
        {
            Console.SetCursorPosition(0, safeRow);
            Console.WriteLine();
        }
        catch
        {
            Console.WriteLine();
        }
    }

    private static void UpdateStatusLine(int row, string text, int columnOffset, int maxWidth)
    {
        int consoleWidth = SafeWindowWidth();
        if (consoleWidth <= 0)
        {
            consoleWidth = 80;
        }

        int width = Math.Max(1, Math.Min(maxWidth, Math.Max(1, consoleWidth - columnOffset)));
        var lines = SplitIntoLines(text);
        int maxLines = Math.Clamp(lines.Count, 1, 3);
        if (lines.Count > maxLines)
        {
            lines = lines.Take(maxLines).ToList();
        }

        for (int i = 0; i < maxLines; i++)
        {
            string content = lines[i];
            if (content.Length > width)
            {
                content = content[..width];
            }
            else
            {
                content = content.PadRight(width);
            }

            try
            {
                int safeRow = Math.Clamp(row + i, 0, SafeBufferHeight() - 1);
                Console.SetCursorPosition(columnOffset, safeRow);
                Console.Write(content);
            }
            catch
            {
                Console.WriteLine(content);
            }
        }
    }

    private static List<string> SplitIntoLines(string text)
    {
        return text
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
            .Where(line => line is not null)
            .ToList();
    }

    private static void ReportMissingArguments()
    {
        string[] args = Environment.GetCommandLineArgs();
        string executable = args.Length > 0 ? Path.GetFileName(args[0]) : "play";
        string providedArgs = args.Length > 1
            ? string.Join(' ', args.Skip(1))
            : "<none>";
        string usage = $"{executable} [--verbose|-v] <audio-file-or-directory>";

        Logger.Error($"No audio file or directory specified.{Environment.NewLine}Usage: {usage}{Environment.NewLine}Arguments: {providedArgs}");
    }

    private static int SafeWindowWidth()
    {
        try
        {
            return Console.WindowWidth;
        }
        catch
        {
            return 80;
        }
    }

    private VisualizerSwitcher CreateVisualizerSwitcher(ConsoleLayout layout, int initialIndex)
    {
        var options = new (string Label, Func<IAudioVisualizer> Factory)[]
        {
            WrapWithTiming(
                "Fire",
                () => new FireEqualizerVisualizer(
                    flameTopRow: layout.VisualTopRow,
                    flameRows: layout.FlameRows,
                    infoRow: -1,
                    columnOffset: layout.FlameColumnOffset),
                layout),
            WrapWithTiming(
                "Bars",
                () => new GraphicEqualizerVisualizer(
                    originRow: layout.VisualTopRow,
                    bandCount: 32,
                    reservedRows: layout.FlameRows,
                    columnOffset: layout.FlameColumnOffset),
                layout),
            WrapWithTiming(
                "Waveform",
                () => new WaveformVisualizer(
                    topRow: layout.VisualTopRow,
                    rows: layout.FlameRows,
                    columnOffset: layout.FlameColumnOffset),
                layout),
            WrapWithTiming(
                "Pixels",
                () => new PixelPulseVisualizer(
                    topRow: layout.VisualTopRow,
                    rows: layout.FlameRows,
                    columnOffset: layout.FlameColumnOffset),
                layout)
        };

        return new VisualizerSwitcher(options, initialIndex);
    }

    private static (string Label, Func<IAudioVisualizer> Factory) WrapWithTiming(
        string label,
        Func<IAudioVisualizer> factory,
        ConsoleLayout layout)
    {
        return (label, () =>
        {
            var inner = factory();
            if (layout.InfoRow < 0)
            {
                return inner;
            }

            return new TimingOverlayVisualizer(inner, layout.InfoRow, layout.FlameColumnOffset);
        });
    }

    private static int SafeCursorTop()
    {
        try
        {
            return Console.CursorTop;
        }
        catch
        {
            return 0;
        }
    }

    private static int SafeBufferHeight()
    {
        try
        {
            return Console.BufferHeight;
        }
        catch
        {
            return 40;
        }
    }

    private static bool CaptureCursorVisibility()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            return Console.CursorVisible;
        }
        catch
        {
            return true;
        }
    }

    private static void TrySetCursorVisible(bool visible)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Console.CursorVisible = visible;
        }
        catch
        {
            // ignored
        }
    }

    private readonly record struct ConsoleLayout(
        int VisualTopRow,
        int ArtTopRow,
        int InfoRow,
        int StatusRow,
        int MetadataColumnOffset,
        int MetadataWidth,
        int FlameColumnOffset,
        int ReservedRows,
        int FlameRows);
}
