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
    private const int AlbumArtWidthChars = AlbumArtPixels;
    private const int MinimumEqColumn = AlbumArtWidthChars + 2;

    private readonly AppOptions _options;

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
        Console.WriteLine("Controls: Left=rewind 5s | Right=fast-forward 5s | Up=previous track | Down=next track | Space=pause/resume | Ctrl+C stop");
        if (playlist.Count <= 1)
        {
            Console.WriteLine("Single track detected: Up/Down arrows remain mapped but will be ignored.");
        }

        Logger.Verbose($"Playlist entries:{Environment.NewLine}{string.Join(Environment.NewLine, Enumerable.Range(0, playlist.Count).Select(i => $"  [{i + 1}] {playlist[i]}"))}");

        var layout = PrepareLayout();
        var albumArtRenderer = new AlbumArtRenderer(layout.ArtTopRow, 0, AlbumArtPixels);

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
            UpdateStatusLine(layout.StatusRow, BuildStatus(currentTrackDisplay, currentIndex, playlist.Count, isPaused), layout.EqColumnOffset);

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
                RenderStatusLine();
                Logger.Info($"Now playing {currentTrack} [{currentIndex + 1}/{playlist.Count}]");

                using var visualizer = new GraphicEqualizerVisualizer(layout.EqTopRow, columnOffset: layout.EqColumnOffset);
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
        string? inputPath = string.IsNullOrWhiteSpace(_options.FilePath)
            ? PromptForAudioFile()
            : _options.FilePath;

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            Logger.Error("Please supply a valid audio file path (.mp3/.wav/.aiff).");
            return null;
        }

        string candidate = inputPath.Trim().Trim('"');
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
        int visualTopRow = ReserveVisualizerArea(AlbumArtRows);
        int artTopRow = visualTopRow;
        int textBlockStart = artTopRow + Math.Max(0, (AlbumArtRows - 3) / 2) - 1;
        if (textBlockStart < artTopRow)
        {
            textBlockStart = artTopRow;
        }

        int eqTopRow = textBlockStart;
        int statusRow = textBlockStart + 2;

        return new ConsoleLayout(
            VisualTopRow: visualTopRow,
            ArtTopRow: artTopRow,
            EqTopRow: eqTopRow,
            StatusRow: statusRow,
            EqColumnOffset: MinimumEqColumn,
            ReservedRows: AlbumArtRows);
    }

    private static string BuildStatus(string path, int index, int total, bool paused)
    {
        string state = paused ? "Paused" : "Now playing";
        var label = TrackMetadata.FromFile(path);

        var builder = new StringBuilder();
        builder.Append($"{state} [{index + 1}/{total}]");
        if (!string.IsNullOrWhiteSpace(label.Title))
        {
            builder.AppendLine();
            builder.Append(label.Title);
        }
        if (!string.IsNullOrWhiteSpace(label.Artist))
        {
            builder.AppendLine();
            builder.Append(label.Artist);
        }
        if (!string.IsNullOrWhiteSpace(label.Album))
        {
            builder.AppendLine();
            builder.Append(label.Album);
        }

        return builder.ToString();
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

    private static void UpdateStatusLine(int row, string text, int columnOffset)
    {
        int consoleWidth = SafeWindowWidth();
        if (consoleWidth <= 0)
        {
            consoleWidth = 80;
        }

        int width = Math.Max(1, consoleWidth - columnOffset);
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

    private static string PromptForAudioFile()
    {
        Console.Write("Drag or enter an audio file path (.mp3/.wav): ");
        return (Console.ReadLine() ?? string.Empty).Trim().Trim('"');
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
        int EqTopRow,
        int StatusRow,
        int EqColumnOffset,
        int ReservedRows);
}
