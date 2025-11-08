using Conplaya.Playback;
using System.Numerics;

namespace Conplaya.Playback.Visualization;

public sealed class GraphicEqualizerVisualizer : IAudioVisualizer
{
    private static readonly char[] LevelGlyphs = { '\u0020', '\u2581', '\u2582', '\u2583', '\u2584', '\u2585', '\u2586', '\u2587', '\u2588' };
    private const char ColumnSeparator = '\u2502';

    private readonly object _consoleLock = new();
    private readonly int _bandCount;
    private readonly string _label;
    private readonly double _minFreq = 30;
    private readonly double _maxFreq = 16000;
    private readonly double _minDb = -65;
    private readonly double _maxDb = 0;
    private readonly double _smoothingFactor = 0.55;
    private readonly int _reservedRows;
    private readonly int _columnOffset;
    private readonly TimeSpan _minFrameInterval = TimeSpan.FromMilliseconds(4);

    private int _baseRow;
    private string _lastEqRender = string.Empty;
    private string _lastTimeRender = string.Empty;
    private DateTime _lastFrameTimestamp = DateTime.MinValue;
    private bool _supportsCursorControl = !Console.IsOutputRedirected;

    private double[]? _window;
    private Complex[]? _fftBuffer;
    private double[]? _smoothedLevels;
    private double[]? _levelScratch;
    private char[]? _renderBuffer;
    private double _windowGain = 1d;
    private int[]? _bandBinStart;
    private int[]? _bandBinEnd;
    private int _lastSampleRate;
    private int _lastFftSize;
    private double _fftNormalizationFactor = 1d;
    private bool _disposed;

    public GraphicEqualizerVisualizer(int originRow, int bandCount = 32, string label = "Graphic EQ", int reservedRows = 2, int columnOffset = 0)
    {
        _bandCount = Math.Clamp(bandCount, 8, 96);
        _label = label;
        _reservedRows = Math.Max(2, reservedRows);
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

        var timingLine = BuildTimingLine(timing, width);
        int targetContentWidth = Math.Max(_label.Length + 1, timingLine.ContentWidth);
        int barArea = Math.Max(1, targetContentWidth - (_label.Length + 1));
        var glyphs = BuildGlyphs(levels, barArea);

        string eqRaw = $"{_label} {new string(glyphs)}";
        string eqContent = FitToExactWidth(eqRaw, targetContentWidth);
        string eqLine = FitToWidth(eqContent, width);
        string timeLine = FitToWidth(timingLine.Text, width);

        int baseRow = EnsureBaseRow();

        lock (_consoleLock)
        {
            WriteLine(eqLine, baseRow, width, ref _lastEqRender);
            WriteLine(timeLine, baseRow + 1, width, ref _lastTimeRender);
        }
    }

    private ReadOnlySpan<char> BuildGlyphs(double[] levels, int activeBands)
    {
        EnsureRenderBuffer(activeBands);
        var buffer = _renderBuffer.AsSpan(0, activeBands);

        if (activeBands == _bandCount)
        {
            for (int i = 0; i < activeBands; i++)
            {
                buffer[i] = LevelToGlyph(levels[i]);
            }

            return buffer;
        }

        double step = (_bandCount - 1d) / Math.Max(1, activeBands - 1);
        for (int i = 0; i < activeBands; i++)
        {
            int sourceIndex = (int)Math.Round(i * step);
            sourceIndex = Math.Clamp(sourceIndex, 0, _bandCount - 1);
            buffer[i] = LevelToGlyph(levels[sourceIndex]);
        }

        return buffer;
    }

    private void EnsureRenderBuffer(int requiredLength)
    {
        if (_renderBuffer == null || _renderBuffer.Length < requiredLength)
        {
            _renderBuffer = new char[requiredLength];
        }
    }

    private static char LevelToGlyph(double level)
    {
        int index = (int)Math.Round(level * (LevelGlyphs.Length - 1));
        index = Math.Clamp(index, 0, LevelGlyphs.Length - 1);
        return LevelGlyphs[index];
    }

    private static TimingLine BuildTimingLine(PlaybackTiming timing, int windowWidth)
    {
        string position = FormatTime(timing.Position);
        string total = FormatTime(timing.TotalDuration);
        string remaining = FormatTime(timing.Remaining);

        string core = $"{position} / {total}  (-{remaining})";
        int available = windowWidth - core.Length - 2;
        if (available < 8)
        {
            string truncated = FitToExactWidth(core, windowWidth);
            int truncatedWidth = Math.Max(1, truncated.TrimEnd().Length);
            return new TimingLine(truncated, truncatedWidth);
        }

        int barWidth = Math.Max(0, Math.Min(available - 2, 40));
        if (barWidth < 1)
        {
            string truncated = FitToExactWidth(core, windowWidth);
            int truncatedWidth = Math.Max(1, truncated.TrimEnd().Length);
            return new TimingLine(truncated, truncatedWidth);
        }

        string bar = BuildProgressBar(timing, barWidth);
        string raw = $"{core} {bar}";
        int contentWidth = Math.Min(windowWidth, raw.Length);
        int bracketIndex = raw.LastIndexOf(']');
        if (bracketIndex >= 0)
        {
            contentWidth = Math.Min(windowWidth, bracketIndex + 1);
        }
        contentWidth = Math.Max(1, contentWidth);

        string fitted = FitToExactWidth(raw, windowWidth);
        return new TimingLine(fitted, contentWidth);
    }

    private static string BuildProgressBar(PlaybackTiming timing, int barWidth)
    {
        if (barWidth <= 0)
        {
            return string.Empty;
        }

        double progress = timing.TotalDuration.TotalMilliseconds > 0
            ? Math.Clamp(timing.Position.TotalMilliseconds / timing.TotalDuration.TotalMilliseconds, 0, 1)
            : 0;

        int filled = (int)Math.Round(progress * barWidth);
        filled = Math.Clamp(filled, 0, barWidth);

        string filledSegment = new string('\u2580', filled);
        string emptySegment = new string('\u2591', barWidth - filled);
        return $"[{filledSegment}{emptySegment}]";
    }

    private static string FitToWidth(string text, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        if (text.Length == width)
        {
            return text;
        }

        if (text.Length > width)
        {
            return text[..width];
        }

        return text + new string(' ', width - text.Length);
    }

    private static string FitToExactWidth(string text, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        if (text.Length == width)
        {
            return text;
        }

        if (text.Length > width)
        {
            return text[..width];
        }

        return text + new string(' ', width - text.Length);
    }

    private readonly record struct TimingLine(string Text, int ContentWidth);

    private static string FormatTime(TimeSpan value)
    {
        value = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        return value.TotalHours >= 1
            ? value.ToString(@"hh\:mm\:ss")
            : value.ToString(@"mm\:ss");
    }

    private static int FrequencyToBin(double freq, int sampleRate, int fftSize)
    {
        double bin = freq / sampleRate * fftSize;
        return (int)Math.Round(bin);
    }

    private void WriteLine(string text, int row, int width, ref string cache)
    {
        string render = BuildFixedWidthLine(text, width);
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
