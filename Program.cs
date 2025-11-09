using System.Text;
using System.IO;
using System.Linq;
using Conplaya.Playback;
using Conplaya.Playback.Control;
using Conplaya.Playback.Visualization;
using Conplaya.Terminal;
using Conplaya.Logging;
using TagLib;

Console.OutputEncoding = Encoding.UTF8;
TerminalCapabilities.EnsureVirtualTerminal();

var options = ParseArguments(args);
Logger.Configure(options.Verbose);

var filePath = string.IsNullOrWhiteSpace(options.FilePath) ? PromptForAudioFile() : options.FilePath;
if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
{
    Logger.Error("Please supply a valid audio file path (.wav, .mp3, .aiff).");
    return 1;
}
Logger.Info($"Starting Conplaya (verbose={(options.Verbose ? "on" : "off")})");

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

const int AlbumArtPixels = 18;
const int AlbumArtRows = AlbumArtPixels / 2;
const int AlbumArtWidthChars = AlbumArtPixels;
const int reservedRows = AlbumArtRows;
const int minimumEqColumn = AlbumArtWidthChars + 2;

int visualTopRow = ReserveVisualizerArea(reservedRows);
int artTopRow = visualTopRow;
int textBlockStart = artTopRow + Math.Max(0, (AlbumArtRows - 3) / 2);
int eqTopRow = textBlockStart;
int statusRow = textBlockStart + 2;
int eqColumnOffset = minimumEqColumn;
bool isPaused = false;
string currentTrackDisplay = string.Empty;

var albumArtRenderer = new AlbumArtRenderer(artTopRow, 0, AlbumArtPixels);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

bool originalCursorVisible = true;
if (OperatingSystem.IsWindows())
{
    try
    {
        originalCursorVisible = Console.CursorVisible;
    }
    catch
    {
        originalCursorVisible = true;
    }

    TrySetCursorVisible(false);
}

int exitCode = 0;
string exitMessage = "Playback finished.";

using var controller = new PlaybackController(TimeSpan.FromSeconds(5));
controller.SetTrackAdvanceEnabled(playlist.Count > 1);

void RenderStatusLine() =>
    UpdateStatusLine(statusRow, BuildStatus(currentTrackDisplay, currentIndex, playlist.Count, isPaused), eqColumnOffset);

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

        using var visualizer = new GraphicEqualizerVisualizer(eqTopRow, columnOffset: eqColumnOffset);
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
            // Handled skip.
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
    MoveCursorPastVisualizer(visualTopRow, reservedRows);
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

static int ReserveVisualizerArea(int rows = 2)
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

static void MoveCursorPastVisualizer(int topRow, int rows)
{
    int targetRow = topRow + rows;
    try
    {
        Console.SetCursorPosition(0, targetRow);
    }
    catch
    {
        Console.WriteLine();
    }
}

static void UpdateStatusLine(int row, string text, int columnOffset)
{
    int consoleWidth = SafeWindowWidth();
    if (consoleWidth <= 0)
    {
        consoleWidth = 80;
    }

    int width = Math.Max(1, consoleWidth - columnOffset);
    var lines = SplitIntoLines(text);
    int maxLines = Math.Max(1, lines.Count);

    for (int i = 0; i < maxLines; i++)
    {
        string content = lines[i];
        if (content.Length > width)
        {
            content = content[..width];
        }
        else
        {
            int totalPadding = width - content.Length;
            int left = totalPadding / 2;
            int right = totalPadding - left;
            content = new string(' ', left) + content + new string(' ', right);
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

static string BuildStatus(string path, int index, int total, bool paused)
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

static int SafeWindowWidth()
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

static int SafeCursorTop()
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

static int SafeBufferHeight()
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

static void TrySetCursorVisible(bool visible)
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

static string PromptForAudioFile()
{
    Console.Write("Drag or enter an audio file path (.mp3/.wav): ");
    return (Console.ReadLine() ?? string.Empty).Trim().Trim('"');
}

static List<string> SplitIntoLines(string text)
{
    return text
        .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
        .Where(line => line is not null)
        .ToList();
}

static AppOptions ParseArguments(string[] args)
{
    var options = new AppOptions();
    foreach (var arg in args)
    {
        if (string.Equals(arg, "--verbose", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "-v", StringComparison.OrdinalIgnoreCase))
        {
            options.Verbose = true;
        }
        else if (string.IsNullOrWhiteSpace(options.FilePath))
        {
            options.FilePath = arg;
        }
    }

    return options;
}

sealed class AppOptions
{
    public bool Verbose { get; set; }
    public string? FilePath { get; set; }
}

sealed class TrackMetadata
{
    public string Title { get; }
    public string Artist { get; }
    public string Album { get; }

    private TrackMetadata(string title, string artist, string album)
    {
        Title = title;
        Artist = artist;
        Album = album;
    }

    public static TrackMetadata FromFile(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            return new TrackMetadata(Path.GetFileName(path), string.Empty, string.Empty);
        }

        try
        {
            using var tagFile = TagLib.File.Create(path);
            string title = string.IsNullOrWhiteSpace(tagFile.Tag.Title) ? Path.GetFileName(path) : tagFile.Tag.Title!;
            string artist = tagFile.Tag.FirstPerformer ?? string.Empty;
            string album = tagFile.Tag.Album ?? string.Empty;
            return new TrackMetadata(title, artist, album);
        }
        catch
        {
            return new TrackMetadata(Path.GetFileName(path), string.Empty, string.Empty);
        }
    }
}
