using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Conplaya.Playback;

internal sealed class Playlist
{
    private static readonly string[] DefaultExtensions = { ".mp3", ".wav", ".aiff", ".aif", ".aac", ".m4a", ".wma", ".flac" };
    private readonly List<string> _tracks;

    private Playlist(List<string> tracks)
    {
        _tracks = tracks;
    }

    public int Count => _tracks.Count;

    public string this[int index] => _tracks[index];

    public int IndexOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return -1;
        }

        string full = Path.GetFullPath(path);
        for (int i = 0; i < _tracks.Count; i++)
        {
            if (string.Equals(_tracks[i], full, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    public int GetNextIndexCircular(int current)
    {
        if (_tracks.Count == 0)
        {
            return -1;
        }

        if (_tracks.Count == 1)
        {
            return 0;
        }

        return (current + 1 + _tracks.Count) % _tracks.Count;
    }

    public int GetPreviousIndexCircular(int current)
    {
        if (_tracks.Count == 0)
        {
            return -1;
        }

        if (_tracks.Count == 1)
        {
            return 0;
        }

        return (current - 1 + _tracks.Count) % _tracks.Count;
    }

    public static Playlist FromSeed(string seedPath)
    {
        if (string.IsNullOrWhiteSpace(seedPath))
        {
            throw new ArgumentException("Seed path must be provided.", nameof(seedPath));
        }

        string fullSeed = Path.GetFullPath(seedPath);
        string? directory = Path.GetDirectoryName(fullSeed);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Directory.GetCurrentDirectory();
        }

        var comparer = StringComparer.OrdinalIgnoreCase;
        List<string> tracks;
        try
        {
            var extensionSet = new HashSet<string>(DefaultExtensions, comparer);
            tracks = Directory
                .EnumerateFiles(directory)
                .Where(file => extensionSet.Contains(Path.GetExtension(file)))
                .Select(Path.GetFullPath)
                .Distinct(comparer)
                .OrderBy(file => file, comparer)
                .ToList();
        }
        catch (IOException)
        {
            tracks = new List<string>();
        }
        catch (UnauthorizedAccessException)
        {
            tracks = new List<string>();
        }

        bool containsSeed = tracks.Any(track => comparer.Equals(track, fullSeed));
        if (tracks.Count == 0 || !containsSeed)
        {
            tracks.Insert(0, fullSeed);
        }

        return new Playlist(tracks);
    }
}
