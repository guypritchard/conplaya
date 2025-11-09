using Conplaya.Logging;
using Conplaya.Playback.Visualization;
using NAudio.Wave;
using System.Numerics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Conplaya.Playback;

public sealed class ConsoleAudioPlayer : IAsyncDisposable
{
    private readonly AudioFileReader _audioReader;
    private readonly WaveOutEvent _waveOut;
    private readonly VisualizationRingBuffer _buffer;
    private readonly VisualizationSampleProvider _sampleProvider;
    private readonly VisualizationEngine _visualEngine;
    private readonly object _stateLock = new();
    private bool _isPaused;

    public ConsoleAudioPlayer(string filePath, IAudioVisualizer visualizer, VisualizationSettings? settings = null)
    {
        if (visualizer is null)
        {
            throw new ArgumentNullException(nameof(visualizer));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Audio file not found.", filePath);
        }

        settings ??= VisualizationSettings.Default;

        Logger.Verbose($"Initializing audio reader for '{filePath}'");
        _audioReader = new AudioFileReader(filePath);
        _waveOut = new WaveOutEvent();

        _buffer = new VisualizationRingBuffer(settings.BufferCapacity);
        _sampleProvider = new VisualizationSampleProvider(_audioReader, _buffer);
        _waveOut.Init(_sampleProvider);

        _visualEngine = new VisualizationEngine(
            _buffer,
            visualizer,
            _audioReader.WaveFormat.SampleRate,
            settings.FrameSize,
            () => new PlaybackTiming(_audioReader.CurrentTime, _audioReader.TotalTime));
    }

    public async Task PlayAsync(CancellationToken token)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var visualizationTask = _visualEngine.RunAsync(linkedCts.Token);

        var playbackCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<StoppedEventArgs>? handler = null;
        handler = (_, args) =>
        {
            if (args.Exception != null)
            {
                playbackCompletion.TrySetException(args.Exception);
            }
            else
            {
                playbackCompletion.TrySetResult(null);
            }
        };

        _waveOut.PlaybackStopped += handler;

        try
        {
            Logger.Info("Starting playback");
            _waveOut.Play();

            Task completedTask = await Task.WhenAny(playbackCompletion.Task, Task.Delay(Timeout.Infinite, token));
            if (completedTask != playbackCompletion.Task)
            {
                _waveOut.Stop();
                Logger.Warn("Playback stopped due to external cancellation");
            }

            await playbackCompletion.Task.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Logger.Info("Playback finished successfully");
        }
        finally
        {
            _waveOut.PlaybackStopped -= handler;
            _buffer.Complete();
            linkedCts.Cancel();

            try
            {
                await visualizationTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Logger.Verbose("Visualization loop cancelled");
            }
        }
    }

    public TimeSpan Position
    {
        get
        {
            lock (_audioReader)
            {
                return _audioReader.CurrentTime;
            }
        }
    }

    public TimeSpan Duration
    {
        get
        {
            lock (_audioReader)
            {
                return _audioReader.TotalTime;
            }
        }
    }

    public void SeekRelative(TimeSpan offset)
    {
        lock (_audioReader)
        {
            var target = _audioReader.CurrentTime + offset;
            if (target < TimeSpan.Zero)
            {
                target = TimeSpan.Zero;
            }
            else if (target > _audioReader.TotalTime)
            {
                target = _audioReader.TotalTime;
            }

            _audioReader.CurrentTime = target;
            _buffer.Reset();
        }
    }

    public bool TogglePause()
    {
        lock (_stateLock)
        {
            if (_isPaused)
            {
                _waveOut.Play();
                _isPaused = false;
                Logger.Info("Resumed playback");
            }
            else
            {
                _waveOut.Pause();
                _isPaused = true;
                Logger.Info("Paused playback");
            }

            return _isPaused;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        _waveOut.Dispose();
        _audioReader.Dispose();
    }
}

public sealed class VisualizationSettings
{
    public static VisualizationSettings Default { get; } = new VisualizationSettings();

    public int FrameSize { get; }
    public int BufferCapacity { get; }

    public VisualizationSettings(int frameSize = 1024, int bufferCapacity = 65536)
    {
        if (!BitOperations.IsPow2(frameSize))
        {
            throw new ArgumentException("Visualization frame size must be a power of two.", nameof(frameSize));
        }

        if (bufferCapacity <= frameSize)
        {
            throw new ArgumentException("Buffer capacity must exceed the frame size.", nameof(bufferCapacity));
        }

        FrameSize = frameSize;
        BufferCapacity = bufferCapacity;
    }
}

public readonly record struct PlaybackTiming(TimeSpan Position, TimeSpan TotalDuration)
{
    public TimeSpan Remaining => TotalDuration > Position
        ? TotalDuration - Position
        : TimeSpan.Zero;
}

internal sealed class VisualizationEngine
{
    private readonly VisualizationRingBuffer _buffer;
    private readonly IAudioVisualizer _visualizer;
    private readonly int _sampleRate;
    private readonly int _frameSize;
    private readonly Func<PlaybackTiming> _timingProvider;
    private readonly float[] _frame;

    public VisualizationEngine(
        VisualizationRingBuffer buffer,
        IAudioVisualizer visualizer,
        int sampleRate,
        int frameSize,
        Func<PlaybackTiming> timingProvider)
    {
        _buffer = buffer;
        _visualizer = visualizer;
        _sampleRate = sampleRate;
        _frameSize = frameSize;
        _timingProvider = timingProvider;
        _frame = new float[_frameSize];
    }

    public Task RunAsync(CancellationToken token)
    {
        return Task.Run(() => RunLoop(token), CancellationToken.None);
    }

    private void RunLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            bool hasFrame;
            try
            {
                hasFrame = FillFrame(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!hasFrame)
            {
                break;
            }

            var timing = _timingProvider();
            _visualizer.Render(_frame, _sampleRate, timing);
        }
    }

    private bool FillFrame(CancellationToken token)
    {
        int offset = 0;

        while (offset < _frame.Length)
        {
            int read = _buffer.Read(_frame, offset, _frame.Length - offset, token);
            if (read == 0)
            {
                if (_buffer.IsCompletelyConsumed)
                {
                    if (offset == 0)
                    {
                        return false;
                    }

                    Array.Clear(_frame, offset, _frame.Length - offset);
                    break;
                }

                continue;
            }

            offset += read;
        }

        return true;
    }
}

internal sealed class VisualizationRingBuffer
{
    private readonly float[] _buffer;
    private readonly object _gate = new();
    private int _writeIndex;
    private int _readIndex;
    private int _count;
    private bool _completed;

    public VisualizationRingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _buffer = new float[capacity];
    }

    public void Write(ReadOnlySpan<float> samples, int channels)
    {
        if (channels <= 0)
        {
            channels = 1;
        }

        lock (_gate)
        {
            for (int i = 0; i < samples.Length; i += channels)
            {
                int availableChannels = Math.Min(channels, samples.Length - i);
                float mono = 0f;
                for (int c = 0; c < availableChannels; c++)
                {
                    mono += samples[i + c];
                }

                mono /= availableChannels;
                Enqueue(mono);
            }

            Monitor.PulseAll(_gate);
        }
    }

    public int Read(float[] destination, int offset, int count, CancellationToken token)
    {
        lock (_gate)
        {
            while (_count == 0 && !_completed)
            {
                Monitor.Wait(_gate, TimeSpan.FromMilliseconds(10));
                token.ThrowIfCancellationRequested();
            }

            if (_count == 0 && _completed)
            {
                return 0;
            }

            int toCopy = Math.Min(count, _count);
            for (int i = 0; i < toCopy; i++)
            {
                destination[offset + i] = _buffer[_readIndex];
                _readIndex = (_readIndex + 1) % _buffer.Length;
            }

            _count -= toCopy;
            return toCopy;
        }
    }

    private void Enqueue(float value)
    {
        if (_count == _buffer.Length)
        {
            _readIndex = (_readIndex + 1) % _buffer.Length;
            _count--;
        }

        _buffer[_writeIndex] = value;
        _writeIndex = (_writeIndex + 1) % _buffer.Length;
        _count++;
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            Monitor.PulseAll(_gate);
        }
    }

    public bool IsCompletelyConsumed
    {
        get
        {
            lock (_gate)
            {
                return _completed && _count == 0;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _writeIndex = 0;
            _readIndex = 0;
            _count = 0;
            _completed = false;
        }
    }
}

internal sealed class VisualizationSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly VisualizationRingBuffer _buffer;
    private readonly int _channels;

    public VisualizationSampleProvider(ISampleProvider source, VisualizationRingBuffer buffer)
    {
        _source = source;
        _buffer = buffer;
        _channels = source.WaveFormat.Channels;
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int samplesRead = _source.Read(buffer, offset, count);
        if (samplesRead > 0)
        {
            _buffer.Write(buffer.AsSpan(offset, samplesRead), _channels);
        }
        else
        {
            _buffer.Complete();
        }

        return samplesRead;
    }
}
