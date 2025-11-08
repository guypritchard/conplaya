using Conplaya.Playback;

namespace Conplaya.Playback.Visualization;

public interface IAudioVisualizer : IDisposable
{
    void Render(ReadOnlySpan<float> frame, int sampleRate, PlaybackTiming timing);
}
