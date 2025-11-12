using System;

namespace Conplaya.Playback.Visualization;

internal interface IThemedVisualizer
{
    void SetPalette(VisualizerPalette palette);
}

public readonly record struct VisualizerColor(byte R, byte G, byte B)
{
    public VisualizerColor Lighten(double amount)
    {
        amount = Math.Clamp(amount, -1, 1);
        double factor = 1 + amount;
        byte Adjust(byte value) => (byte)Math.Clamp(value * factor, 0, 255);
        return new VisualizerColor(Adjust(R), Adjust(G), Adjust(B));
    }
}

public readonly record struct VisualizerPalette(VisualizerColor Base, VisualizerColor Accent)
{
    public static VisualizerPalette Default { get; } = new(
        new VisualizerColor(90, 160, 255),
        new VisualizerColor(20, 50, 120));
}
