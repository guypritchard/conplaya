using System;
using System.Numerics;
using System.Text;
using Conplaya.Playback;

namespace Conplaya.Playback.Visualization;

public sealed class FireEqualizerVisualizer : IAudioVisualizer
{
    private const char PixelGlyph = '\u2580';
    private const string AnsiReset = "\u001b[0m";

    private readonly object _renderLock = new();
    private readonly Random _random = new();
    private readonly int _bandCount;
    private readonly int _topRow;
    private readonly int _infoRow;
    private readonly int _columnOffset;
    private readonly int _charRows;
    private readonly TimeSpan _minFrameInterval = TimeSpan.FromMilliseconds(16);

    private readonly double _minFreq = 30;
    private readonly double _maxFreq = 16000;
    private readonly double _minDb = -65;
    private readonly double _maxDb = 0;
    private readonly double _smoothingFactor = 0.6;

    private double[]? _window;
    private Complex[]? _fftBuffer;
    private double[]? _smoothedLevels;
    private double[]? _levelScratch;
    private int[]? _bandBinStart;
    private int[]? _bandBinEnd;
    private double[]? _columnEnergy;
    private double _windowGain = 1d;
    private double _fftNormalizationFactor = 1d;
    private int _lastSampleRate;
    private int _lastFftSize;

    private byte[]? _fireState;
    private byte[]? _fireScratch;
    private int _cachedWidth;
    private string _lastTimingRender = string.Empty;
    private StringBuilder? _lineBuilder;
    private DateTime _lastFrameTimestamp = DateTime.MinValue;
    private bool _supportsCursorControl = !Console.IsOutputRedirected;
    private bool _disposed;

    public FireEqualizerVisualizer(
        int flameTopRow,
        int flameRows,
        int infoRow,
        int columnOffset,
        int bandCount = 48)
    {
        _topRow = Math.Max(0, flameTopRow);
        _charRows = Math.Max(2, flameRows);
        _infoRow = infoRow;
        _columnOffset = Math.Max(0, columnOffset);
        _bandCount = Math.Clamp(bandCount, 12, 96);
    }

    public void Render(ReadOnlySpan<float> frame, int sampleRate, PlaybackTiming timing)
    {
        if (_disposed)
        {
            return;
        }

        EnsureFftBuffers(frame.Length);
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

        int width = Math.Max(1, SafeWindowWidth() - _columnOffset);
        if (width != _cachedWidth)
        {
            _cachedWidth = width;
            _fireState = null;
        }

        EnsureFireBuffers(width);
        PopulateColumnEnergy(levels, width);
        UpdateFireField(width);
        RenderFrame(width, timing);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_renderLock)
        {
            int width = Math.Max(1, SafeWindowWidth() - _columnOffset);
            int totalRows = _charRows + 2;
            for (int i = 0; i < totalRows; i++)
            {
                if (!TrySetCursor(_topRow + i, _columnOffset))
                {
                    break;
                }

                Console.Write(new string(' ', width));
            }
        }
    }

    private void EnsureFftBuffers(int length)
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

    private void PopulateColumnEnergy(double[] levels, int width)
    {
        if (_columnEnergy == null || _columnEnergy.Length != width)
        {
            _columnEnergy = new double[width];
        }

        if (width == levels.Length)
        {
            Array.Copy(levels, _columnEnergy, width);
            return;
        }

        if (width == 1)
        {
            _columnEnergy[0] = levels[0];
            return;
        }

        double step = (levels.Length - 1d) / Math.Max(1, width - 1);
        for (int i = 0; i < width; i++)
        {
            int sourceIndex = (int)Math.Round(i * step);
            sourceIndex = Math.Clamp(sourceIndex, 0, levels.Length - 1);
            _columnEnergy[i] = levels[sourceIndex];
        }
    }

    private void EnsureFireBuffers(int width)
    {
        int pixelRows = _charRows * 2;
        int length = width * pixelRows;
        if (_fireState != null && _fireState.Length == length && _fireScratch != null && _fireScratch.Length == length)
        {
            return;
        }

        _fireState = new byte[length];
        _fireScratch = new byte[length];
    }

    private void UpdateFireField(int width)
    {
        if (_fireState is null || _fireScratch is null || _columnEnergy is null)
        {
            return;
        }

        int pixelRows = _charRows * 2;
        var source = _fireState.AsSpan();
        var target = _fireScratch.AsSpan();
        target.Clear();

        int bottomOffset = (pixelRows - 1) * width;
        for (int column = 0; column < width; column++)
        {
            double energy = Math.Clamp(_columnEnergy[column] + (RandomDeviation() * 0.2), 0, 1);
            byte value = (byte)Math.Clamp(Math.Round(energy * 255), 0, 255);
            source[bottomOffset + column] = value;
            target[bottomOffset + column] = value;
        }

        for (int row = pixelRows - 2; row >= 0; row--)
        {
            int rowOffset = row * width;
            int belowOffset = (row + 1) * width;

            for (int column = 0; column < width; column++)
            {
                byte below = source[belowOffset + column];
                if (below == 0)
                {
                    continue;
                }

                int decay = _random.Next(1, 4);
                int spread = _random.Next(-1, 2);
                int targetColumn = column + spread;
                if (targetColumn < 0 || targetColumn >= width)
                {
                    targetColumn = column;
                }

                int newValue = below - (decay * 7);
                if (newValue < 0)
                {
                    newValue = 0;
                }

                int targetIndex = rowOffset + targetColumn;
                if (newValue > target[targetIndex])
                {
                    target[targetIndex] = (byte)newValue;
                }
            }
        }

        (_fireState, _fireScratch) = (_fireScratch, _fireState);
    }

    private void RenderFrame(int width, PlaybackTiming timing)
    {
        if (_fireState is null)
        {
            return;
        }

        lock (_renderLock)
        {
            DrawFlames(width);
            DrawTimingLine(timing, width);
        }
    }

    private void DrawFlames(int width)
    {
        if (!_supportsCursorControl)
        {
            return;
        }

        var fireState = _fireState;
        if (fireState is null)
        {
            return;
        }

        var builder = _lineBuilder ??= new StringBuilder(width * 32);
        builder.EnsureCapacity(width * 32);
        int pixelRows = _charRows * 2;

        for (int row = 0; row < _charRows; row++)
        {
            if (!TrySetCursor(_topRow + row, _columnOffset))
            {
                break;
            }

            builder.Clear();
            int topOffset = row * 2 * width;
            int bottomOffset = topOffset + width;

            for (int column = 0; column < width; column++)
            {
                var topColor = EvaluatePalette(fireState[topOffset + column]);
                var bottomColor = EvaluatePalette(fireState[bottomOffset + column]);
                AppendPixel(builder, topColor, bottomColor);
            }

            builder.Append(AnsiReset);
            Console.Write(builder.ToString());
        }
    }

    private void DrawTimingLine(PlaybackTiming timing, int width)
    {
        if (_infoRow < 0)
        {
            return;
        }

        string line = BuildTimingLine(timing, width);
        if (string.Equals(line, _lastTimingRender, StringComparison.Ordinal))
        {
            return;
        }

        if (!TrySetCursor(_infoRow, _columnOffset))
        {
            return;
        }

        Console.Write(line);
        _lastTimingRender = line;
    }

    private static string BuildTimingLine(PlaybackTiming timing, int width)
    {
        int safeWidth = Math.Max(1, width);
        string position = FormatTime(timing.Position);
        string total = FormatTime(timing.TotalDuration);
        string remaining = FormatTime(timing.Remaining);
        string baseText = $"{position} / {total}  (-{remaining})";

        int barWidth = Math.Max(0, safeWidth - baseText.Length - 2);
        string bar = BuildProgressBar(timing, barWidth);
        string content = $"{baseText} {bar}";
        return FitToWidth(content, safeWidth);
    }

    private static string BuildProgressBar(PlaybackTiming timing, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        double progress = timing.TotalDuration.TotalMilliseconds > 0
            ? Math.Clamp(timing.Position.TotalMilliseconds / timing.TotalDuration.TotalMilliseconds, 0, 1)
            : 0;

        int filled = (int)Math.Round(progress * width);
        filled = Math.Clamp(filled, 0, width);

        string filledSegment = new string('\u2588', filled);
        string emptySegment = new string('\u2591', width - filled);
        return $"[{filledSegment}{emptySegment}]";
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

    private static void AppendPixel(StringBuilder builder, Color24 top, Color24 bottom)
    {
        builder
            .Append("\u001b[38;2;")
            .Append(top.R).Append(';').Append(top.G).Append(';').Append(top.B).Append('m')
            .Append("\u001b[48;2;")
            .Append(bottom.R).Append(';').Append(bottom.G).Append(';').Append(bottom.B).Append('m')
            .Append(PixelGlyph);
    }

    private static Color24 EvaluatePalette(byte intensity)
    {
        double normalized = intensity / 255d;
        double scaled = normalized * (Palette.Length - 1);
        int index = (int)Math.Floor(scaled);
        double fraction = scaled - index;

        var start = Palette[Math.Clamp(index, 0, Palette.Length - 1)];
        var end = Palette[Math.Clamp(index + 1, 0, Palette.Length - 1)];

        byte Lerp(byte a, byte b) => (byte)Math.Round(a + ((b - a) * fraction));
        return new Color24(Lerp(start.R, end.R), Lerp(start.G, end.G), Lerp(start.B, end.B));
    }

    private static double RandomDeviation() => (Random.Shared.NextDouble() - 0.5) * 0.5;

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

    private readonly struct Color24
    {
        public Color24(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;
        }

        public byte R { get; }
        public byte G { get; }
        public byte B { get; }
    }

    private static readonly Color24[] Palette =
    {
        new(0, 0, 0),
        new(7, 7, 7),
        new(31, 7, 7),
        new(47, 15, 7),
        new(71, 15, 7),
        new(87, 23, 7),
        new(103, 31, 7),
        new(119, 31, 7),
        new(143, 39, 7),
        new(159, 47, 7),
        new(175, 63, 7),
        new(191, 71, 7),
        new(199, 71, 7),
        new(223, 79, 7),
        new(223, 87, 7),
        new(223, 95, 7),
        new(215, 103, 15),
        new(215, 111, 15),
        new(207, 119, 15),
        new(207, 127, 15),
        new(207, 135, 23),
        new(199, 143, 23),
        new(199, 151, 31),
        new(191, 159, 31),
        new(191, 167, 39),
        new(191, 175, 47),
        new(183, 183, 47),
        new(199, 199, 79),
        new(223, 223, 111),
        new(239, 239, 159),
        new(247, 247, 191),
        new(255, 255, 255)
    };
}
