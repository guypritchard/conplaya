using System.Numerics;

namespace Conplaya.Playback.Visualization;

internal static class Fft
{
    public static void Forward(Complex[] buffer)
    {
        if (buffer is null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        int length = buffer.Length;
        if (length == 0)
        {
            return;
        }

        if (!BitOperations.IsPow2(length))
        {
            throw new ArgumentException("FFT input length must be a power of two.", nameof(buffer));
        }

        BitReverse(buffer);

        for (int step = 2; step <= length; step <<= 1)
        {
            double angle = -2 * Math.PI / step;
            var wPhaseStep = new Complex(Math.Cos(angle), Math.Sin(angle));

            for (int start = 0; start < length; start += step)
            {
                var w = Complex.One;
                int halfStep = step / 2;

                for (int i = 0; i < halfStep; i++)
                {
                    var even = buffer[start + i];
                    var odd = buffer[start + i + halfStep] * w;
                    buffer[start + i] = even + odd;
                    buffer[start + i + halfStep] = even - odd;
                    w *= wPhaseStep;
                }
            }
        }
    }

    private static void BitReverse(Complex[] buffer)
    {
        int length = buffer.Length;
        int target = 0;

        for (int position = 0; position < length; position++)
        {
            if (position < target)
            {
                (buffer[position], buffer[target]) = (buffer[target], buffer[position]);
            }

            int bit = length >> 1;
            while ((target & bit) != 0)
            {
                target ^= bit;
                bit >>= 1;
            }

            target ^= bit;
        }
    }
}
