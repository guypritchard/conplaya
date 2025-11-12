using System;
using System.IO;
using System.Threading;
using Conplaya.Playback;

namespace Conplaya.Playback.Control;

internal enum TrackAdvance
{
    None,
    Next,
    Previous,
}

internal sealed class PlaybackController : IDisposable
{
    private readonly TimeSpan _seekStep;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _inputThread;
    private ConsoleAudioPlayer? _player;
    private Action<TrackAdvance>? _trackAdvanceHandler;
    private Action<bool>? _pauseChangedHandler;
    private Action? _visualizerToggleHandler;
    private volatile bool _allowTrackAdvance = true;

    public PlaybackController(TimeSpan seekStep)
    {
        _seekStep = seekStep;
        _inputThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "Playback Controller",
        };
        _inputThread.Start();
    }

    public void AttachPlayer(ConsoleAudioPlayer? player)
    {
        Interlocked.Exchange(ref _player, player);
    }

    public void SetTrackAdvanceHandler(Action<TrackAdvance>? handler)
    {
        Interlocked.Exchange(ref _trackAdvanceHandler, handler);
    }

    public void SetPauseChangedHandler(Action<bool>? handler)
    {
        Interlocked.Exchange(ref _pauseChangedHandler, handler);
    }

    public void SetVisualizationToggleHandler(Action? handler)
    {
        Interlocked.Exchange(ref _visualizerToggleHandler, handler);
    }

    public void SetTrackAdvanceEnabled(bool enabled)
    {
        _allowTrackAdvance = enabled;
    }

    private void ReadLoop()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(25);
                    continue;
                }

                var key = Console.ReadKey(true);
                HandleKey(key);
            }
            catch (InvalidOperationException)
            {
                Thread.Sleep(200);
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
        }
    }

    private void HandleKey(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.LeftArrow:
                SeekRelative(-_seekStep);
                break;
            case ConsoleKey.RightArrow:
                SeekRelative(_seekStep);
                break;
            case ConsoleKey.UpArrow:
                if (_allowTrackAdvance)
                {
                    RequestTrackAdvance(TrackAdvance.Previous);
                }
                break;
            case ConsoleKey.DownArrow:
                if (_allowTrackAdvance)
                {
                    RequestTrackAdvance(TrackAdvance.Next);
                }
                break;
            case ConsoleKey.Spacebar:
                TogglePause();
                break;
            case ConsoleKey.V:
                ToggleVisualizer();
                break;
        }
    }

    private void SeekRelative(TimeSpan delta)
    {
        var player = Volatile.Read(ref _player);
        player?.SeekRelative(delta);
    }

    private void TogglePause()
    {
        var player = Volatile.Read(ref _player);
        if (player is null)
        {
            return;
        }

        bool isPaused = player.TogglePause();
        var handler = Volatile.Read(ref _pauseChangedHandler);
        handler?.Invoke(isPaused);
    }

    private void RequestTrackAdvance(TrackAdvance advance)
    {
        var handler = Volatile.Read(ref _trackAdvanceHandler);
        handler?.Invoke(advance);
    }

    private void ToggleVisualizer()
    {
        var handler = Volatile.Read(ref _visualizerToggleHandler);
        handler?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_inputThread.IsAlive)
        {
            _inputThread.Join(TimeSpan.FromSeconds(1));
        }
    }
}
