using System.Linq;
using System.Text;
using Conplaya.Playback;

namespace Conplaya.Playback.Visualization;

internal sealed class WaveformVisualizer : IAudioVisualizer, IThemedVisualizer
{
    private const char HalfBlock = '\u2580';

    private readonly int _topRow;
    private readonly int _charRows;
    private readonly int _pixelRows;
    private readonly int _columnOffset;
    private readonly TimeSpan _minFrameInterval = TimeSpan.FromMilliseconds(12);
    private readonly double _smoothing = 0.65;

    private double[]? _columns;
    private double[]? _scratch;
    private string[]? _lineCache;
    private VisualizerPalette _palette = VisualizerPalette.Default;
    private bool _supportsCursorControl = !Console.IsOutputRedirected;
    private DateTime _lastFrameTimestamp = DateTime.MinValue;
    private bool _disposed;

    public WaveformVisualizer(int topRow, int rows, int columnOffset)
    {
        _topRow = Math.Max(0, topRow);
        _charRows = Math.Max(1, rows);
        _pixelRows = _charRows * 2;
        _columnOffset = Math.Max(0, columnOffset);
    }

    public void SetPalette(VisualizerPalette palette) => _palette = palette;

    public void Render(ReadOnlySpan<float> frame, int sampleRate, PlaybackTiming timing)
    {
        if (_disposed)
        {
            return;
        }

        if (!ShouldRenderFrame())
        {
            return;
        }

        int width = Math.Max(1, SafeWindowWidth() - _columnOffset);
        EnsureBuffers(width);
        ProjectSamples(frame, width);
        RenderLines(width);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearArea();
    }

    private void ProjectSamples(ReadOnlySpan<float> frame, int width)
    {
        var columns = _columns!;
        var scratch = _scratch!;

        Array.Fill(scratch, 0);
        int samplesPerColumn = Math.Max(1, frame.Length / (width * 2));

        for (int i = 0; i < width; i++)
        {
            int start = i * samplesPerColumn;
            int end = Math.Min(frame.Length, start + samplesPerColumn);
            if (start >= end)
            {
                scratch[i] = 0;
                continue;
            }

            double aggregate = 0;
            for (int s = start; s < end; s++)
            {
                aggregate += frame[s];
            }

            double average = aggregate / (end - start);
            scratch[i] = Math.Clamp(average * 1.6, -1, 1);
        }

        for (int i = 0; i < width; i++)
        {
            double previous = columns[i];
            double sample = scratch[i];
            if (double.IsNaN(previous))
            {
                columns[i] = sample;
            }
            else
            {
                columns[i] = (previous * _smoothing) + (sample * (1 - _smoothing));
            }
        }
    }

    private void RenderLines(int width)
    {
        var columns = _columns!;
        EnsureLineCache();

        lock (this)
        {
            for (int row = 0; row < _charRows; row++)
            {
                string line = BuildPixelLine(columns, row);
                WriteLine(line, _topRow + row, width, ref _lineCache![row]);
            }
        }
    }

    private string BuildPixelLine(double[] columns, int charRow)
    {
        int topPixel = charRow * 2;
        int bottomPixel = Math.Min(_pixelRows - 1, topPixel + 1);

        var builder = new StringBuilder(columns.Length * 24);
        for (int column = 0; column < columns.Length; column++)
        {
            double amplitude = columns[column];
            double topIntensity = SampleIntensity(amplitude, topPixel);
            double bottomIntensity = SampleIntensity(amplitude, bottomPixel);

            var topColor = EvaluateColor(topIntensity, amplitude);
            var bottomColor = EvaluateColor(bottomIntensity, amplitude);
            AppendPixel(builder, topColor, bottomColor);
        }

        builder.Append(AnsiReset);
        return builder.ToString();
    }

    private double SampleIntensity(double amplitude, int pixelRow)
    {
        double normalized = (amplitude + 1) * 0.5;
        double target = (1 - normalized) * (_pixelRows - 1);
        double distance = Math.Abs(pixelRow - target);
        double thickness = Math.Max(0.6, _pixelRows * 0.08);
        double intensity = Math.Clamp(1 - (distance / thickness), 0, 1);

        double baseline = Math.Abs(pixelRow - ((_pixelRows - 1) / 2d));
        double baselineGlow = Math.Clamp(1 - (baseline / 1.2), 0, 1) * 0.15;
        return Math.Clamp(intensity + baselineGlow, 0, 1);
    }

    private Color24 EvaluateColor(double intensity, double amplitude)
    {
        var baseColor = _palette.Base;
        var accentColor = _palette.Accent;

        byte Lerp(byte a, byte b, double t) => (byte)Math.Clamp(a + ((b - a) * t), 0, 255);
        double mix = Math.Clamp(intensity, 0, 1);

        byte r = Lerp(baseColor.R, accentColor.R, mix);
        byte g = Lerp(baseColor.G, accentColor.G, mix);
        byte b = Lerp(baseColor.B, accentColor.B, mix);

        double highlight = (amplitude + 1) * 0.5;
        r = (byte)Math.Clamp(r + highlight * 60, 0, 255);
        g = (byte)Math.Clamp(g + intensity * 40, 0, 255);
        b = (byte)Math.Clamp(b + (1 - intensity) * 20, 0, 255);

        return new Color24(r, g, b);
    }

    private void EnsureBuffers(int width)
    {
        if (_columns is null || _columns.Length != width)
        {
            _columns = Enumerable.Repeat(double.NaN, width).ToArray();
            _scratch = new double[width];
        }
        else if (_scratch is null || _scratch.Length != width)
        {
            _scratch = new double[width];
        }
    }

    private void EnsureLineCache()
    {
        if (_lineCache is null || _lineCache.Length != _charRows)
        {
            _lineCache = Enumerable.Repeat(string.Empty, _charRows).ToArray();
        }
    }

    private bool ShouldRenderFrame()
    {
        var now = DateTime.UtcNow;
        if (now - _lastFrameTimestamp < _minFrameInterval)
        {
            return false;
        }

        _lastFrameTimestamp = now;
        return true;
    }

    private void WriteLine(string text, int row, int width, ref string cache)
    {
        string render = BuildPaddedRow(text, width);
        if (string.Equals(cache, render, StringComparison.Ordinal))
        {
            return;
        }

        if (!TrySetCursor(row, _columnOffset))
        {
            return;
        }

        Console.Write(render);
        cache = render;
    }

    private static string BuildPaddedRow(string text, int width)
    {
        int max = Math.Max(1, width);
        int length = GetVisibleLength(text);

        if (length >= max)
        {
            return text;
        }

        return text + new string(' ', max - length);
    }

    private static int GetVisibleLength(string text)
    {
        int length = 0;
        bool escape = false;

        foreach (char c in text)
        {
            if (escape)
            {
                if (c == 'm')
                {
                    escape = false;
                }
                continue;
            }

            if (c == '\u001b')
            {
                escape = true;
                continue;
            }

            length++;
        }

        return length;
    }

    private void AppendPixel(StringBuilder builder, Color24 top, Color24 bottom)
    {
        builder
            .Append("\u001b[38;2;")
            .Append(top.R).Append(';').Append(top.G).Append(';').Append(top.B).Append('m')
            .Append("\u001b[48;2;")
            .Append(bottom.R).Append(';').Append(bottom.G).Append(';').Append(bottom.B).Append('m')
            .Append(HalfBlock);
    }

    private void ClearArea()
    {
        int width = Math.Max(1, SafeWindowWidth() - _columnOffset);
        string blank = new string(' ', width);

        for (int row = 0; row < _charRows; row++)
        {
            if (!TrySetCursor(_topRow + row, _columnOffset))
            {
                break;
            }

            Console.Write(blank);
        }
    }

    private bool TrySetCursor(int row, int column)
    {
        if (!_supportsCursorControl)
        {
            return false;
        }

        try
        {
            int safeRow = Math.Clamp(row, 0, SafeBufferHeight() - 1);
            int safeColumn = Math.Clamp(column, 0, Math.Max(0, SafeWindowWidth() - 1));
            Console.SetCursorPosition(safeColumn, safeRow);
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

    public readonly record struct Color24(byte R, byte G, byte B);
    private const string AnsiReset = "\u001b[0m";

}
