using System.IO.Compression;
using System.Text.Json;

namespace Cropsmith.Core;

public static class VideoProbe
{
    public static VideoInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToList();

        var video = streams.FirstOrDefault(s => Type(s) == "video");
        if (video.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("No video stream found in this file.");

        var audio = streams.FirstOrDefault(s => Type(s) == "audio");
        var duration = TimeSpan.Zero;
        if (doc.RootElement.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var d)
            && double.TryParse(d.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var secs))
            duration = TimeSpan.FromSeconds(secs);

        return new VideoInfo(
            video.GetProperty("width").GetInt32(),
            video.GetProperty("height").GetInt32(),
            duration,
            audio.ValueKind == JsonValueKind.Undefined ? null : audio.GetProperty("codec_name").GetString(),
            streams.Any(s => Type(s) == "subtitle"));
    }

    /// <summary>As <see cref="Parse"/>, plus whether the subtitles are on by default in the source.</summary>
    public static VideoInfo ParseFull(string json)
    {
        var info = Parse(json);
        using var doc = JsonDocument.Parse(json);
        var onByDefault = doc.RootElement.GetProperty("streams").EnumerateArray()
            .Where(s => Type(s) == "subtitle")
            .Any(s => s.TryGetProperty("disposition", out var d) && d.TryGetProperty("default", out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() == 1);
        return info with { SubtitlesOnByDefault = onByDefault };
    }

    private static string? Type(JsonElement s) => s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
}

public static class FfmpegLocator
{
    public static string? Find(string exeName, IEnumerable<string> directories, Func<string, bool> exists) =>
        directories.Where(d => !string.IsNullOrWhiteSpace(d))
                   .Select(d => Path.Combine(d.Trim(), exeName))
                   .FirstOrDefault(exists);
}

public static class ZipExtractor
{
    private static readonly string[] Wanted = ["ffmpeg.exe", "ffprobe.exe"];

    public static bool ExtractBinaries(string zipPath, string destination)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var hits = zip.Entries.Where(e => Wanted.Contains(e.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        if (!hits.Any(e => e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))) return false;

        Directory.CreateDirectory(destination);
        foreach (var e in hits) e.ExtractToFile(Path.Combine(destination, e.Name.ToLowerInvariant()), overwrite: true);
        return true;
    }
}
