namespace Conplaya.Logging;

internal static class Logger
{
    private static readonly object _gate = new();
    private static bool _verbose;

    public static void Configure(bool verbose) => _verbose = verbose;

    public static void Info(string message)
    {
        if (_verbose)
        {
            Write("INFO", message);
        }
    }

    public static void Warn(string message)
    {
        if (_verbose)
        {
            Write("WARN", message);
        }
    }

    public static void Verbose(string message)
    {
        if (_verbose)
        {
            Write("VERBOSE", message);
        }
    }

    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        lock (_gate)
        {
            Console.WriteLine($"[{level}] {message}");
        }
    }
}
