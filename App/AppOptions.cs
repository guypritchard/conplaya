namespace Conplaya.App;

internal sealed class AppOptions
{
    public bool Verbose { get; init; }
    public string? FilePath { get; init; }

    public static AppOptions Parse(string[] args)
    {
        bool verbose = false;
        string? filePath = null;

        foreach (string arg in args)
        {
            if (string.Equals(arg, "--verbose", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "-v", StringComparison.OrdinalIgnoreCase))
            {
                verbose = true;
            }
            else if (string.IsNullOrWhiteSpace(filePath))
            {
                filePath = arg;
            }
        }

        return new AppOptions
        {
            Verbose = verbose,
            FilePath = filePath
        };
    }
}
