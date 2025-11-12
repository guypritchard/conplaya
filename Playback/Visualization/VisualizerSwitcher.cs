using System;
using System.Collections.Generic;
using System.Linq;

namespace Conplaya.Playback.Visualization;

internal sealed class VisualizerSwitcher : IAudioVisualizer
{
    private readonly IReadOnlyList<Func<IAudioVisualizer>> _factories;
    private readonly IReadOnlyList<string> _labels;
    private readonly object _swapGate = new();

    private IAudioVisualizer _active;
    private IAudioVisualizer? _pending;
    private int _currentIndex;
    private int _pendingIndex = -1;
    private bool _disposed;
    private VisualizerPalette _currentPalette = VisualizerPalette.Default;

    public VisualizerSwitcher(
        IReadOnlyList<(string Label, Func<IAudioVisualizer> Factory)> options,
        int initialIndex = 0)
    {
        if (options is null || options.Count == 0)
        {
            throw new ArgumentException("At least one visualizer option is required.", nameof(options));
        }

        _labels = options.Select(o => o.Label).ToArray();
        _factories = options.Select(o => o.Factory).ToArray();

        _currentIndex = Math.Clamp(initialIndex, 0, _factories.Count - 1);
        _active = CreateVisualizer(_currentIndex);
    }

    public string CurrentLabel => _labels[_currentIndex];

    public int CurrentIndex => _currentIndex;

    public int CycleNext()
    {
        if (_disposed)
        {
            return _currentIndex;
        }

        lock (_swapGate)
        {
            int next = (_currentIndex + 1) % _factories.Count;
            _pending?.Dispose();
            _pending = CreateVisualizer(next);
            _pendingIndex = next;
            return next;
        }
    }

    public void Render(ReadOnlySpan<float> frame, int sampleRate, PlaybackTiming timing)
    {
        if (_disposed)
        {
            return;
        }

        SwapIfNeeded();
        _active.Render(frame, sampleRate, timing);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_swapGate)
        {
            _pending?.Dispose();
            _active.Dispose();
            _pending = null;
        }
    }

    private void SwapIfNeeded()
    {
        IAudioVisualizer? toDispose = null;

        lock (_swapGate)
        {
            if (_pending is null)
            {
                return;
            }

            toDispose = _active;
            _active = _pending;
            _currentIndex = _pendingIndex;
            _pending = null;
            _pendingIndex = -1;
        }

        toDispose?.Dispose();
    }

    public void SetPalette(VisualizerPalette palette)
    {
        _currentPalette = palette;

        lock (_swapGate)
        {
            ApplyPalette(_active);
            if (_pending is not null)
            {
                ApplyPalette(_pending);
            }
        }
    }

    private IAudioVisualizer CreateVisualizer(int index)
    {
        var visualizer = _factories[index]();
        ApplyPalette(visualizer);
        return visualizer;
    }

    private void ApplyPalette(IAudioVisualizer visualizer)
    {
        if (visualizer is IThemedVisualizer themed)
        {
            themed.SetPalette(_currentPalette);
        }
    }
}
