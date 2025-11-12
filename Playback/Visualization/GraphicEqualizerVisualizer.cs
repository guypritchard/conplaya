using System.Linq;
using System.Drawing;
using System.Numerics;
using System.Text;
using Conplaya.Playback;

namespace Conplaya.Playback.Visualization;

public sealed class GraphicEqualizerVisualizer : IAudioVisualizer, IThemedVisualizer
{
    private readonly object _consoleLock = new();
    private readonly int _bandCount;
    private readonly double _minFreq = 30;
    private readonly double _maxFreq = 16000;
    private readonly double _minDb = -65;
    private readonly double _maxDb = 0;
    private readonly double _smoothingFactor = 0.55;
    private readonly int _reservedRows;
    private readonly int _columnOffset;
    private readonly int _pixelRows;
    private readonly TimeSpan _minFrameInterval = TimeSpan.FromMilliseconds(4);

    private int _baseRow;
    private string[]? _lineCache;
    private VisualizerPalette _palette = VisualizerPalette.Default;
    private DateTime _lastFrameTimestamp = DateTime.MinValue;
    private bool _supportsCursorControl = !Console.IsOutputRedirected;

    private double[]? _window;
    private Complex[]? _fftBuffer;
    private double[]? _smoothedLevels;
    private double[]? _levelScratch;
    private double[]? _columnScratch;
    private double _windowGain = 1d;
    private int[]? _bandBinStart;
    private int[]? _bandBinEnd;
    private int _lastSampleRate;
    private int _lastFftSize;
    private double _fftNormalizationFactor = 1d;
    private bool _disposed;

    public GraphicEqualizerVisualizer(int originRow, int bandCount = 32, int reservedRows = 2, int columnOffset = 0)
    {
        _bandCount = Math.Clamp(bandCount, 8, 96);
        _reservedRows = Math.Max(2, reservedRows);
        _pixelRows = Math.Max(2, _reservedRows * 2);
        _baseRow = NormalizeBaseRow(originRow);
        _columnOffset = Math.Max(0, columnOffset);
    }

    public void Render(ReadOnlySpan<float> frame, int sampleRate, PlaybackTiming timing)
    {
        if (_disposed)
        {
            return;
        }

        EnsureBuffers(frame.Length);
        if (!ShouldRenderFrame())
        {
            return;
        }

        Span<Complex> fftSpan = _fftBuffer!;

        for (int i = 0; i < frame.Length; i++)
        {
            double windowed = frame[i] * _window![i] * _windowGain;
            fftSpan[i] = new Complex(windowed, 0);
        }

        Fft.Forward(_fftBuffer!);
        EnsureBandBins(sampleRate);
        var levels = ComputeBandLevels();
        RenderToConsole(levels, timing);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        int baseRow = EnsureBaseRow();

        lock (_consoleLock)
        {
            int width = Math.Max(1, SafeWindowWidth() - _columnOffset);
            for (int row = 0; row < _reservedRows; row++)
            {
                if (!TrySetCursor(baseRow + row, _columnOffset))
                {
                    break;
                }

                Console.Write(new string(' ', width));
            }
        }
    }

    public void SetPalette(VisualizerPalette palette) => _palette = palette;

    private void EnsureBuffers(int length)
    {
        if (_window != null && _window.Length == length)
        {
            return;
        }

        _window = new double[length];
        _fftBuffer = new Complex[length];
        _smoothedLevels = new double[_bandCount];
        _levelScratch = new double[_bandCount];

        double sum = 0;
        for (int i = 0; i < length; i++)
        {
            double value = 0.5 * (1 - Math.Cos((2 * Math.PI * i) / (length - 1)));
            _window[i] = value;
            sum += value;
        }

        _windowGain = sum <= 0 ? 1d : length / sum;
        _lastFftSize = length;
        _bandBinStart = null;
        _bandBinEnd = null;
    }

    private void EnsureBandBins(int sampleRate)
    {
        if (_fftBuffer is null)
        {
            throw new InvalidOperationException("FFT buffer is not initialized.");
        }

        if (_bandBinStart != null && _bandBinEnd != null && sampleRate == _lastSampleRate && _fftBuffer.Length == _lastFftSize)
        {
            return;
        }

        int fftSize = _fftBuffer.Length;
        int half = Math.Max(1, fftSize / 2);
        double nyquist = sampleRate / 2d;
        double effectiveMax = Math.Min(_maxFreq, nyquist);
        double logMin = Math.Log10(Math.Max(10, _minFreq));
        double logMax = Math.Log10(Math.Max(_minFreq + 10, effectiveMax));

        _bandBinStart = new int[_bandCount];
        _bandBinEnd = new int[_bandCount];

        for (int band = 0; band < _bandCount; band++)
        {
            double t0 = band / (double)_bandCount;
            double t1 = (band + 1) / (double)_bandCount;

            double freqStart = Math.Pow(10, logMin + (logMax - logMin) * t0);
            double freqEnd = Math.Pow(10, logMin + (logMax - logMin) * t1);

            int binStart = FrequencyToBin(freqStart, sampleRate, fftSize);
            int binEnd = Math.Max(binStart + 1, FrequencyToBin(freqEnd, sampleRate, fftSize));
            binStart = Math.Clamp(binStart, 0, half - 1);
            binEnd = Math.Clamp(binEnd, binStart + 1, half);

            _bandBinStart[band] = binStart;
            _bandBinEnd[band] = binEnd;
        }

        double normalizationBase = Math.Max(1d, half);
        _fftNormalizationFactor = 1d / (normalizationBase * normalizationBase);
        _lastSampleRate = sampleRate;
        _lastFftSize = fftSize;
    }

    private double[] ComputeBandLevels()
    {
        if (_fftBuffer is null || _smoothedLevels is null || _levelScratch is null || _bandBinStart is null || _bandBinEnd is null)
        {
            throw new InvalidOperationException("Visualizer buffers are not initialized.");
        }

        for (int band = 0; band < _bandCount; band++)
        {
            int binStart = _bandBinStart[band];
            int binEnd = _bandBinEnd[band];
            double energy = 0;

            for (int bin = binStart; bin < binEnd; bin++)
            {
                double real = _fftBuffer[bin].Real;
                double imaginary = _fftBuffer[bin].Imaginary;
                energy += (real * real) + (imaginary * imaginary);
            }

            energy *= _fftNormalizationFactor;
            int width = Math.Max(1, binEnd - binStart);
            double rms = Math.Sqrt(Math.Max(energy / width, 1e-12));
            double db = 20 * Math.Log10(rms);
            db = Math.Clamp(db, _minDb, _maxDb);
            double normalized = (db - _minDb) / (_maxDb - _minDb);

            double previous = _smoothedLevels[band];
            if (previous <= 0)
            {
                previous = normalized;
            }

            double smoothed = (previous * (1 - _smoothingFactor)) + (normalized * _smoothingFactor);
            _smoothedLevels[band] = smoothed;
            _levelScratch[band] = smoothed;
        }

        return _levelScratch;
    }

    private void RenderToConsole(double[] levels, PlaybackTiming timing)
    {
        int consoleWidth = SafeWindowWidth();
        int width = Math.Max(1, consoleWidth - _columnOffset);
        EnsureLineCache();
        var columnLevels = BuildColumnLevels(levels, width);
        int baseRow = EnsureBaseRow();

        lock (_consoleLock)
        {
            for (int row = 0; row < _reservedRows; row++)
            {
                string line = BuildBarLine(columnLevels, row, _reservedRows);
                WriteLine(line, baseRow + row, width, ref _lineCache![row]);
            }
        }
    }

    private void EnsureLineCache()
    {
        if (_lineCache is null || _lineCache.Length != _reservedRows)
        {
            _lineCache = Enumerable.Repeat(string.Empty, _reservedRows).ToArray();
        }
    }

    private double[] BuildColumnLevels(double[] levels, int width)
    {
        EnsureColumnScratch(width);
        var target = _columnScratch!;

        if (width == _bandCount)
        {
            Array.Copy(levels, target, Math.Min(levels.Length, width));
            if (width > levels.Length)
            {
                Array.Fill(target, levels[^1], levels.Length, width - levels.Length);
            }
            return target;
        }

        double step = (_bandCount - 1d) / Math.Max(1, width - 1);
        for (int i = 0; i < width; i++)
        {
            int sourceIndex = (int)Math.Round(i * step);
            sourceIndex = Math.Clamp(sourceIndex, 0, _bandCount - 1);
            target[i] = levels[sourceIndex];
        }

        return target;
    }

    private void EnsureColumnScratch(int width)
    {
        if (_columnScratch is null || _columnScratch.Length < width)
        {
            _columnScratch = new double[width];
        }
    }

    private string BuildBarLine(double[] levels, int rowIndex, int totalRows)
    {
        int totalPixels = totalRows * 2;
        double topPixel = rowIndex * 2;
        double bottomPixel = Math.Min(totalPixels - 1, topPixel + 1);
        var builder = new StringBuilder(levels.Length * 24);

        for (int i = 0; i < levels.Length; i++)
        {
            double height = Math.Clamp(levels[i], 0, 1) * totalPixels;
            double topAmount = Math.Clamp(height - topPixel, 0, 1);
            double bottomAmount = Math.Clamp(height - bottomPixel, 0, 1);

            var topColor = EvaluateColor(topAmount);
            var bottomColor = EvaluateColor(bottomAmount * 0.75);
            AppendPixel(builder, topColor, bottomColor);
        }

        builder.Append("\u001b[0m");
        return builder.ToString();
    }

    private void AppendPixel(StringBuilder builder, Color top, Color bottom)
    {
        builder
            .Append("\u001b[38;2;")
            .Append(top.R).Append(';').Append(top.G).Append(';').Append(top.B).Append('m')
            .Append("\u001b[48;2;")
            .Append(bottom.R).Append(';').Append(bottom.G).Append(';').Append(bottom.B).Append('m')
            .Append('\u2580');
    }

    private Color EvaluateColor(double intensity)
    {
        var accent = _palette.Accent;
        var baseColor = _palette.Base;
        byte Lerp(byte a, byte b, double t) => (byte)Math.Clamp(a + ((b - a) * t), 0, 255);

        double t = Math.Clamp(intensity, 0, 1);
        byte r = Lerp(baseColor.R, accent.R, t);
        byte g = Lerp(baseColor.G, accent.G, t);
        byte b = Lerp(baseColor.B, accent.B, t);
        return Color.FromArgb(r, g, b);
    }

    private Color EvaluateShadow(double intensity)
    {
        var color = EvaluateColor(intensity * 0.8);
        return Color.FromArgb(
            (byte)Math.Clamp(color.R * 0.6, 0, 255),
            (byte)Math.Clamp(color.G * 0.6, 0, 255),
            (byte)Math.Clamp(color.B * 0.6, 0, 255));
    }

    private static int FrequencyToBin(double freq, int sampleRate, int fftSize)
    {
        double bin = freq / sampleRate * fftSize;
        return (int)Math.Round(bin);
    }

    private void WriteLine(string text, int row, int width, ref string cache)
    {
        string render = text.IndexOf('\u001b') >= 0
            ? BuildAnsiPaddedLine(text, width)
            : BuildFixedWidthLine(text, width);
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

    private int EnsureBaseRow()
    {
        int normalized = NormalizeBaseRow(_baseRow);
        if (normalized != _baseRow)
        {
            _baseRow = normalized;
        }

        return _baseRow;
    }

    private int NormalizeBaseRow(int requestedRow)
    {
        int maxStart = Math.Max(0, SafeBufferHeight() - _reservedRows);
        return Math.Clamp(requestedRow, 0, maxStart);
    }

    private static string BuildFixedWidthLine(string text, int width)
    {
        int max = Math.Max(1, width);
        if (text.Length >= max)
        {
            return text.Length > max ? text[..max] : text;
        }

        return text + new string(' ', max - text.Length);
    }

    private static string BuildAnsiPaddedLine(string text, int width)
    {
        int visible = GetVisibleLength(text);
        if (visible >= width)
        {
            return text;
        }

        return text + new string(' ', width - visible);
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
            return Math.Max(2, SafeWindowHeightFallback());
        }
    }

    private static int SafeWindowHeightFallback()
    {
        try
        {
            return Console.WindowHeight;
        }
        catch
        {
            return 25;
        }
    }
}
