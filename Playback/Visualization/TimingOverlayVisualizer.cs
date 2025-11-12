using System.Text;
using Conplaya.Playback;

namespace Conplaya.Playback.Visualization;

internal sealed class TimingOverlayVisualizer : IAudioVisualizer, IThemedVisualizer
{
    private static readonly bool GlowEnabled = false;
    private readonly IAudioVisualizer _inner;
    private readonly int _row;
    private readonly int _columnOffset;
    private readonly object _renderLock = new();
    private string _lastPlainLine = string.Empty;
    private VisualizerPalette _palette = VisualizerPalette.Default;
    private double _phase;
    private static readonly string AnsiReset = "\u001b[0m";

    public TimingOverlayVisualizer(IAudioVisualizer inner, int row, int columnOffset)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _row = row;
        _columnOffset = Math.Max(0, columnOffset);
    }

    public void Render(ReadOnlySpan<float> frame, int sampleRate, PlaybackTiming timing)
    {
        _inner.Render(frame, sampleRate, timing);
        if (_row < 0)
        {
            return;
        }

        RenderTimingLine(timing);
    }

    public void Dispose() => _inner.Dispose();

    public void SetPalette(VisualizerPalette palette)
    {
        _palette = palette;
        _lastPlainLine = string.Empty;
        if (_inner is IThemedVisualizer themed)
        {
            themed.SetPalette(palette);
        }
    }

    private void RenderTimingLine(PlaybackTiming timing)
    {
        int width = Math.Max(1, SafeWindowWidth() - _columnOffset);
        double phase = 0;
        if (GlowEnabled)
        {
            UpdatePhase();
            phase = _phase;
        }

        var (plain, colored) = BuildTimingLine(timing, width, _palette, phase, GlowEnabled);

        lock (_renderLock)
        {
            if (string.Equals(plain, _lastPlainLine, StringComparison.Ordinal))
            {
                return;
            }

            if (!TrySetCursor(_row, _columnOffset))
            {
                return;
            }

            Console.Write(colored);
            _lastPlainLine = plain;
        }
    }

    private static (string Plain, string Colored) BuildTimingLine(PlaybackTiming timing, int width, VisualizerPalette palette, double phase, bool animated)
    {
        int safeWidth = Math.Max(1, width);
        string position = FormatTime(timing.Position);
        string total = FormatTime(timing.TotalDuration);
        string remaining = FormatTime(timing.Remaining);
        string baseText = $"{position} / {total}  (-{remaining})";

        int barWidth = Math.Max(0, safeWidth - baseText.Length - 1);
        string bar = BuildProgressBar(timing, barWidth);
        string content = barWidth > 0 ? $"{baseText} {bar}" : baseText;
        string plain = FitToWidth(content, safeWidth);

        var builder = new StringBuilder(plain.Length + 32);
        VisualizerColor textColor = palette.Base.Lighten(0.25);
        VisualizerColor accentColor = palette.Accent;
        VisualizerColor emptyColor = palette.Base.Lighten(-0.55);
        VisualizerColor bracketColor = palette.Accent.Lighten(-0.1);
        VisualizerColor? currentColor = null;

        for (int i = 0; i < plain.Length; i++)
        {
            char c = plain[i];
            VisualizerColor baseColor = c switch
            {
                '\u2503' => bracketColor,
                '\u2588' => accentColor,
                '\u2591' => emptyColor,
                _ => textColor
            };

            VisualizerColor targetColor = baseColor;
            if (animated)
            {
                var parameters = c switch
                {
                    '\u2503' => (0.6, 0.35, 0.4, 12d),
                    '\u2588' => (0.0, 0.55, 0.35, 10d),
                    '\u2591' => (1.4, 0.35, 0.45, 11d),
                    _ => (2.2, 0.45, 0.4, 9d)
                };

                var (offset, boost, falloff, focus) = parameters;

                targetColor = EvaluateGlow(
                    baseColor,
                    phase + offset,
                    i,
                    plain.Length,
                    boost,
                    falloff,
                    focus);
            }

            if (currentColor is null || currentColor.Value != targetColor)
            {
                AppendColor(builder, targetColor);
                currentColor = targetColor;
            }

            builder.Append(c);
        }

        builder.Append(AnsiReset);
        return (plain, builder.ToString());
    }

    private static VisualizerColor EvaluateGlow(
        VisualizerColor baseColor,
        double phase,
        int column,
        int totalColumns,
        double intensityBoost = 0.5,
        double falloff = 0.5,
        double focus = 8)
    {
        if (totalColumns <= 1)
        {
            return baseColor;
        }

        double position = column / (double)(totalColumns - 1);
        double center = (Math.Sin(phase) + 1) * 0.5;
        double distance = Math.Abs(position - center);
        double envelope = Math.Exp(-Math.Pow(distance * focus, 2));
        double amount = (envelope * (0.6 + intensityBoost)) - falloff;
        amount = Math.Clamp(amount, -0.5, 0.9);
        return baseColor.Lighten(amount);
    }

    private static string BuildProgressBar(PlaybackTiming timing, int totalWidth)
    {
        if (totalWidth <= 0)
        {
            return string.Empty;
        }

        if (totalWidth < 2)
        {
            totalWidth = 2;
        }

        int interior = Math.Max(0, totalWidth - 2);

        double progress = timing.TotalDuration.TotalMilliseconds > 0
            ? Math.Clamp(timing.Position.TotalMilliseconds / timing.TotalDuration.TotalMilliseconds, 0, 1)
            : 0;

        int filled = interior > 0 ? (int)Math.Round(progress * interior) : 0;
        filled = Math.Clamp(filled, 0, interior);

        string filledSegment = new string('\u2588', filled);
        string emptySegment = new string('\u2591', interior - filled);
        return $"\u2503{filledSegment}{emptySegment}\u2503";
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

    private static string FormatTime(TimeSpan value)
    {
        value = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        return value.TotalHours >= 1
            ? value.ToString(@"hh\:mm\:ss")
            : value.ToString(@"mm\:ss");
    }

    private static void AppendColor(StringBuilder builder, VisualizerColor color)
    {
        builder
            .Append("\u001b[38;2;")
            .Append(color.R).Append(';').Append(color.G).Append(';').Append(color.B).Append('m');
    }

    private void UpdatePhase()
    {
        _phase += 0.015;
        if (_phase > Math.PI * 2)
        {
            _phase -= Math.PI * 2;
        }
    }

    private bool TrySetCursor(int row, int column)
    {
        try
        {
            int safeRow = Math.Clamp(row, 0, SafeBufferHeight() - 1);
            int safeColumn = Math.Clamp(column, 0, Math.Max(0, SafeWindowWidth() - 1));
            Console.SetCursorPosition(safeColumn, safeRow);
            return true;
        }
        catch
        {
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
}
