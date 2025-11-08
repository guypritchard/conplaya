using System;
using System.Runtime.InteropServices;

namespace Conplaya.Terminal;

internal static class TerminalCapabilities
{
    private const int StdOutputHandle = -11;
    private const int EnableVirtualTerminalProcessing = 0x0004;

    private static bool _initialized;

    public static void EnsureVirtualTerminal()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        IntPtr handle = GetStdHandle(StdOutputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return;
        }

        if (!GetConsoleMode(handle, out int mode))
        {
            return;
        }

        if ((mode & EnableVirtualTerminalProcessing) != 0)
        {
            return;
        }

        _ = SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out int lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, int dwMode);
}
