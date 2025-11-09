using System.IO;
using TagLib;

namespace Conplaya.App;

internal sealed class TrackMetadata
{
    public string Title { get; }
    public string Artist { get; }
    public string Album { get; }

    private TrackMetadata(string title, string artist, string album)
    {
        Title = title;
        Artist = artist;
        Album = album;
    }

    public static TrackMetadata FromFile(string path)
    {
        string fallback = Path.GetFileName(path);
        if (!System.IO.File.Exists(path))
        {
            return new TrackMetadata(fallback, string.Empty, string.Empty);
        }

        try
        {
            using var tagFile = TagLib.File.Create(path);
            string title = string.IsNullOrWhiteSpace(tagFile.Tag.Title) ? fallback : tagFile.Tag.Title!;
            string artist = tagFile.Tag.FirstPerformer ?? string.Empty;
            string album = tagFile.Tag.Album ?? string.Empty;
            return new TrackMetadata(title, artist, album);
        }
        catch
        {
            return new TrackMetadata(fallback, string.Empty, string.Empty);
        }
    }
}
