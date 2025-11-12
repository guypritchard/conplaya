using System.Linq;
using System.Text;
using Conplaya.Playback;

namespace Conplaya.Playback.Visualization;

internal sealed class PixelPulseVisualizer : IAudioVisualizer, IThemedVisualizer
{
    private const char HalfBlock = '\u2580';
    private const string AnsiReset = "\u001b[0m";

    private readonly int _topRow;
    private readonly int _charRows;
    private readonly int _pixelRows;
    private readonly int _columnOffset;
    private readonly TimeSpan _minFrameInterval = TimeSpan.FromMilliseconds(16);
    private readonly Random _random = new();

    private double[]? _energy;
    private double[]? _smoothedEnergy;
    private double[]? _phase;
    private string[]? _lineCache;
    private VisualizerPalette _palette = VisualizerPalette.Default;
    private double _globalPhase;

    private DateTime _lastFrameTimestamp = DateTime.MinValue;
    private bool _supportsCursorControl = !Console.IsOutputRedirected;
    private bool _disposed;

    public PixelPulseVisualizer(int topRow, int rows, int columnOffset)
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
        MapEnergy(frame, width);
        UpdatePhase(width);
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

    private void MapEnergy(ReadOnlySpan<float> frame, int width)
    {
        var energy = _energy!;
        int samplesPerColumn = Math.Max(1, frame.Length / width);

        for (int column = 0; column < width; column++)
        {
            int start = column * samplesPerColumn;
            int end = Math.Min(frame.Length, start + samplesPerColumn);
            if (start >= end)
            {
                energy[column] = 0;
                continue;
            }

            double power = 0;
            for (int i = start; i < end; i++)
            {
                power += frame[i] * frame[i];
            }

            double value = Math.Sqrt(power / (end - start));
            energy[column] = Math.Clamp(value, 0, 1);
        }

        var smoothed = _smoothedEnergy!;
        for (int i = 0; i < width; i++)
        {
            double target = energy[i];
            double previous = smoothed[i];
            smoothed[i] = previous <= 0 ? target : (previous * 0.8) + (target * 0.2);
        }
    }

    private void UpdatePhase(int width)
    {
        var phase = _phase!;
        var energy = _smoothedEnergy!;

        _globalPhase += 0.05;
        if (_globalPhase > Math.PI * 2)
        {
            _globalPhase -= Math.PI * 2;
        }

        for (int i = 0; i < width; i++)
        {
            double delta = 0.03 + (energy[i] * 0.5);
            phase[i] += delta;
            if (phase[i] > Math.PI * 2)
            {
                phase[i] -= Math.PI * 2;
            }
        }
    }

    private void RenderLines(int width)
    {
        EnsureLineCache();

        lock (this)
        {
            for (int row = 0; row < _charRows; row++)
            {
                string line = BuildPixelLine(row, width);
                WriteLine(line, _topRow + row, width, ref _lineCache![row]);
            }
        }
    }

    private string BuildPixelLine(int charRow, int width)
    {
        int topPixel = charRow * 2;
        int bottomPixel = Math.Min(_pixelRows - 1, topPixel + 1);

        var builder = new StringBuilder(width * 32);
        var phase = _phase!;
        var energy = _smoothedEnergy!;

        for (int column = 0; column < width; column++)
        {
            double baseEnergy = energy[column];
            double p = phase[column];

            var topColor = SampleColor(topPixel, baseEnergy, p);
            var bottomColor = SampleColor(bottomPixel, baseEnergy, p + 0.3);
            AppendPixel(builder, topColor, bottomColor);
        }

        builder.Append(AnsiReset);
        return builder.ToString();
    }

    private Color24 SampleColor(int pixelRow, double energy, double phase)
    {
        double vertical = pixelRow / (double)Math.Max(1, _pixelRows - 1);
        double sparkle = 0.5 + 0.5 * Math.Sin(phase + (vertical * 3.2) + _globalPhase);
        double pulse = Math.Clamp((energy * 0.7) + (sparkle * 0.5), 0, 1);
        double accentMix = Math.Clamp(vertical * 0.6 + energy * 0.4, 0, 1);

        var baseColor = _palette.Base;
        var accentColor = _palette.Accent;
        byte Lerp(byte a, byte b, double t) => (byte)Math.Clamp(a + ((b - a) * t), 0, 255);

        byte r = Lerp(baseColor.R, accentColor.R, accentMix);
        byte g = Lerp(baseColor.G, accentColor.G, accentMix);
        byte b = Lerp(baseColor.B, accentColor.B, accentMix);

        r = (byte)Math.Clamp(r + pulse * 80, 0, 255);
        g = (byte)Math.Clamp(g + pulse * 60, 0, 255);
        b = (byte)Math.Clamp(b + (1 - vertical) * 40, 0, 255);

        return new Color24(r, g, b);
    }

    private void EnsureBuffers(int width)
    {
        if (_energy is null || _energy.Length != width)
        {
            _energy = new double[width];
            _smoothedEnergy = new double[width];
            _phase = Enumerable.Range(0, width).Select(_ => _random.NextDouble() * Math.PI * 2).ToArray();
        }
        else if (_phase is null || _phase.Length != width)
        {
            _phase = Enumerable.Range(0, width).Select(_ => _random.NextDouble() * Math.PI * 2).ToArray();
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
        int visible = GetVisibleLength(text);

        if (visible >= max)
        {
            return text;
        }

        return text + new string(' ', max - visible);
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

    private readonly record struct Color24(byte R, byte G, byte B);
}
