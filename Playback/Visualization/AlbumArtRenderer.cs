using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Runtime.Versioning;
using TagLib;
using Conplaya.Logging;

namespace Conplaya.Playback.Visualization;

internal sealed class AlbumArtRenderer
{
    private const char PixelGlyph = '\u2580';
    private const string AnsiReset = "\u001b[0m";

    private readonly int _topRow;
    private readonly int _leftColumn;
    private readonly int _pixelSize;
    private readonly int _targetColumns;
    private readonly object _renderLock = new();

    private string? _currentTrack;
    private Color24[,]? _currentPixels;
    private bool _supportsCursorControl = !Console.IsOutputRedirected;

    public VisualizerPalette CurrentPalette { get; private set; } = VisualizerPalette.Default;

    public AlbumArtRenderer(int topRow, int leftColumn = 0, int pixelSize = 24, int targetColumns = 0)
    {
        _topRow = Math.Max(0, topRow);
        _leftColumn = Math.Max(0, leftColumn);
        _pixelSize = Math.Max(2, pixelSize - (pixelSize % 2));
        int effectiveTarget = targetColumns <= 0 ? _pixelSize : targetColumns;
        _targetColumns = Math.Max(_pixelSize, effectiveTarget);
    }

    public void Render(string trackPath)
    {
        if (string.IsNullOrWhiteSpace(trackPath))
        {
            return;
        }

        lock (_renderLock)
        {
            if (!string.Equals(_currentTrack, trackPath, StringComparison.OrdinalIgnoreCase))
            {
                Color24[,]? artwork = null;
                if (OperatingSystem.IsWindows())
                {
                    artwork = LoadArtwork(trackPath);
                }
                _currentPixels = artwork ?? BuildPlaceholder(trackPath);
                CurrentPalette = BuildPalette(_currentPixels);
                _currentTrack = trackPath;
            }

            if (_currentPixels is null)
            {
                return;
            }

            DrawPixels(_currentPixels);
        }
    }

    [SupportedOSPlatform("windows")]
    private Color24[,]? LoadArtwork(string trackPath)
    {
        try
        {
            using var file = TagLib.File.Create(trackPath);
            var picture = file.Tag?.Pictures?.FirstOrDefault(p => p?.Data?.Count > 0);
            Stream? sourceStream = null;

            if (picture is not null)
            {
                sourceStream = new MemoryStream(picture.Data.Data);
            }
            else
            {
                string? directory = Path.GetDirectoryName(trackPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    string fallbackPath = Path.Combine(directory, "folder.jpg");
                    if (System.IO.File.Exists(fallbackPath))
                    {
                        Logger.Verbose($"Using folder art fallback at '{fallbackPath}'");
                        sourceStream = System.IO.File.OpenRead(fallbackPath);
                    }
                }
            }

            if (sourceStream is null)
            {
                Logger.Verbose("No artwork or folder.jpg found; using placeholder.");
                return null;
            }

            using var stream = sourceStream;
            using var source = Image.FromStream(stream, useEmbeddedColorManagement: true, validateImageData: true);
            using var resized = new Bitmap(_pixelSize, _pixelSize);
            using (var graphics = Graphics.FromImage(resized))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.SmoothingMode = SmoothingMode.None;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, _pixelSize, _pixelSize));
            }

            var pixels = new Color24[_pixelSize, _pixelSize];
            for (int y = 0; y < _pixelSize; y++)
            {
                for (int x = 0; x < _pixelSize; x++)
                {
                    var color = resized.GetPixel(x, y);
                    pixels[y, x] = new Color24(color.R, color.G, color.B);
                }
            }

            return pixels;
        }
        catch (UnsupportedFormatException)
        {
            return null;
        }
        catch (CorruptFileException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private Color24[,] BuildPlaceholder(string trackPath)
    {
        int seed = HashCode.Combine(trackPath.ToLowerInvariant());
        var random = new Random(seed);

        Color24 ColorFromRandom() => new(
            (byte)random.Next(32, 220),
            (byte)random.Next(32, 220),
            (byte)random.Next(32, 220));

        var start = ColorFromRandom();
        var end = ColorFromRandom();

        var pixels = new Color24[_pixelSize, _pixelSize];
        for (int y = 0; y < _pixelSize; y++)
        {
            double ty = y / (double)(_pixelSize - 1);
            for (int x = 0; x < _pixelSize; x++)
            {
                double tx = x / (double)(_pixelSize - 1);
                double mix = (tx + ty) / 2d;
                pixels[y, x] = Lerp(start, end, mix);
            }
        }

        // Add a simple grid overlay for texture.
        for (int y = 0; y < _pixelSize; y += 4)
        {
            for (int x = 0; x < _pixelSize; x++)
            {
                pixels[y, x] = Darken(pixels[y, x], 0.15f);
            }
        }

        for (int x = 0; x < _pixelSize; x += 4)
        {
            for (int y = 0; y < _pixelSize; y++)
            {
                pixels[y, x] = Darken(pixels[y, x], 0.15f);
            }
        }

        return pixels;
    }

    private void DrawPixels(Color24[,] pixels)
    {
        if (!_supportsCursorControl)
        {
            return;
        }

        int maxRows = Math.Min(_pixelSize / 2, Math.Max(1, SafeBufferHeight() - _topRow));
        int availableColumns = Math.Max(1, SafeWindowWidth() - _leftColumn);
        int maxColumns = Math.Min(_pixelSize, availableColumns);
        int desiredColumns = Math.Min(_targetColumns, availableColumns);

        double rowScale = (_pixelSize / 2d) / maxRows;
        double columnScale = _pixelSize / (double)maxColumns;

        for (int row = 0; row < maxRows; row++)
        {
            if (!TrySetCursor(row, _leftColumn))
            {
                break;
            }

            int topY = Math.Min(_pixelSize - 1, (int)Math.Round(row * 2 * rowScale));
            int bottomY = Math.Min(_pixelSize - 1, topY + 1);

            var builder = new StringBuilder(maxColumns * 24);
            for (int column = 0; column < maxColumns; column++)
            {
                int srcX = Math.Min(_pixelSize - 1, (int)Math.Round(column * columnScale));
                var topColor = pixels[topY, srcX];
                var bottomColor = pixels[bottomY, srcX];
                AppendPixel(builder, topColor, bottomColor);
            }

            int pad = Math.Max(0, desiredColumns - maxColumns);
            if (pad > 0)
            {
                builder.Append(new string(' ', pad));
            }

            builder.Append(AnsiReset);
            Console.Write(builder.ToString());
        }
    }

    private static VisualizerPalette BuildPalette(Color24[,] pixels)
    {
        int height = pixels.GetLength(0);
        int width = pixels.GetLength(1);
        int total = Math.Max(1, height * width);

        double sumR = 0;
        double sumG = 0;
        double sumB = 0;
        var luminance = new List<(double Value, Color24 Color)>(total);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var color = pixels[y, x];
                sumR += color.R;
                sumG += color.G;
                sumB += color.B;

                double lum = (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B);
                luminance.Add((lum, color));
            }
        }

        var baseColor = new VisualizerColor(
            (byte)Math.Clamp(sumR / total, 0, 255),
            (byte)Math.Clamp(sumG / total, 0, 255),
            (byte)Math.Clamp(sumB / total, 0, 255));

        int accentCount = Math.Max(1, total / 6);
        var accentSample = luminance
            .OrderByDescending(l => l.Value)
            .Take(accentCount)
            .Select(l => l.Color)
            .ToList();

        if (accentSample.Count == 0)
        {
            return new VisualizerPalette(baseColor, baseColor);
        }

        double accentR = accentSample.Average(c => c.R);
        double accentG = accentSample.Average(c => c.G);
        double accentB = accentSample.Average(c => c.B);

        var accent = new VisualizerColor(
            (byte)Math.Clamp(accentR, 0, 255),
            (byte)Math.Clamp(accentG, 0, 255),
            (byte)Math.Clamp(accentB, 0, 255));

        return new VisualizerPalette(baseColor, accent);
    }

    private static void AppendPixel(StringBuilder builder, Color24 top, Color24 bottom)
    {
        builder
            .Append("\u001b[38;2;")
            .Append(top.R).Append(';').Append(top.G).Append(';').Append(top.B).Append('m')
            .Append("\u001b[48;2;")
            .Append(bottom.R).Append(';').Append(bottom.G).Append(';').Append(bottom.B).Append('m')
            .Append(PixelGlyph);
    }

    private bool TrySetCursor(int rowOffset, int column)
    {
        if (!_supportsCursorControl)
        {
            return false;
        }

        try
        {
            int row = Math.Clamp(_topRow + rowOffset, 0, SafeBufferHeight() - 1);
            Console.SetCursorPosition(column, row);
            return true;
        }
        catch
        {
            _supportsCursorControl = false;
            return false;
        }
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

    private static Color24 Lerp(Color24 start, Color24 end, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte LerpChannel(byte a, byte b) => (byte)Math.Round(a + ((b - a) * t));

        return new Color24(
            LerpChannel(start.R, end.R),
            LerpChannel(start.G, end.G),
            LerpChannel(start.B, end.B));
    }

    private static Color24 Darken(Color24 color, float factor)
    {
        factor = Math.Clamp(factor, 0f, 1f);
        return new Color24(
            (byte)Math.Round(color.R * (1 - factor)),
            (byte)Math.Round(color.G * (1 - factor)),
            (byte)Math.Round(color.B * (1 - factor)));
    }

    private readonly struct Color24
    {
        public readonly byte R;
        public readonly byte G;
        public readonly byte B;

        public Color24(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;
        }
    }
}
