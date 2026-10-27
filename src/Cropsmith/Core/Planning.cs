using System.Globalization;
using System.Text.RegularExpressions;

namespace Cropsmith.Core;

public readonly record struct CropBox(int X, int Y, int Width, int Height)
{
    public long Area => (long)Width * Height;
}

public enum CropMode { Smart, Ratio, Manual }

public enum Anchor { Center, TopLeft, Custom }

public sealed record VideoInfo(int Width, int Height, TimeSpan Duration, string? AudioCodec, bool HasSubtitles)
{
    /// <summary>Whether the source's subtitle track is flagged to show by default. Only filled in by <see cref="VideoProbe.ParseFull"/>.</summary>
    public bool SubtitlesOnByDefault { get; init; }
}

public sealed record CropPlan(CropBox Crop, int ScaleWidth, int ScaleHeight, int OutWidth, int OutHeight, string PadColor)
{
    public bool NeedsScale => ScaleWidth != Crop.Width || ScaleHeight != Crop.Height;
    public bool NeedsPad => OutWidth != ScaleWidth || OutHeight != ScaleHeight;
    public double ScaleFactor => (double)ScaleWidth / Crop.Width;
}

public sealed record DetectedContent(CropBox Box, string PadColor);

public static partial class CropDetectParser
{
    [GeneratedRegex(@"x1:(\d+)\s+x2:(\d+)\s+y1:(\d+)\s+y2:(\d+)")]
    private static partial Regex EdgesRx();

    private const int TargetSamples = 120;

    /// <summary>Bounding box covering every cropdetect sample, so no frame loses content.</summary>
    public static CropBox? Union(string ffmpegOutput)
    {
        int x1 = int.MaxValue, y1 = int.MaxValue, x2 = -1, y2 = -1;
        foreach (Match m in EdgesRx().Matches(ffmpegOutput))
        {
            x1 = Math.Min(x1, int.Parse(m.Groups[1].Value));
            x2 = Math.Max(x2, int.Parse(m.Groups[2].Value));
            y1 = Math.Min(y1, int.Parse(m.Groups[3].Value));
            y2 = Math.Max(y2, int.Parse(m.Groups[4].Value));
        }
        return x2 < 0 ? null : new CropBox(x1, y1, x2 - x1 + 1, y2 - y1 + 1);
    }

    /// <summary>Frames-per-second to sample so detection looks at ~120 frames however long the video is.</summary>
    public static string SampleFps(TimeSpan duration)
    {
        var fps = duration.TotalSeconds <= 0 ? 1 : Math.Min(1, TargetSamples / duration.TotalSeconds);
        return fps.ToString("0.#######", CultureInfo.InvariantCulture);
    }
}

public static class BackgroundResolver
{
    /// <summary>Content detected against a white and a black background; the tighter box is the real one.</summary>
    public static DetectedContent? Choose(CropBox? whiteBackground, CropBox? blackBackground)
    {
        if (whiteBackground is null && blackBackground is null) return null;
        if (blackBackground is null) return new(whiteBackground!.Value, "white");
        if (whiteBackground is null) return new(blackBackground.Value, "black");
        return whiteBackground.Value.Area <= blackBackground.Value.Area
            ? new(whiteBackground.Value, "white")
            : new(blackBackground.Value, "black");
    }
}

public static class CropPlanner
{
    private const double AspectTolerance = 0.01;

    public static CropPlan Smart(CropBox content, int targetWidth, int targetHeight, string padColor)
    {
        RequireEven(targetWidth, targetHeight);
        var contentAspect = (double)content.Width / content.Height;
        var targetAspect = (double)targetWidth / targetHeight;

        if (Math.Abs(contentAspect - targetAspect) / targetAspect <= AspectTolerance)
            return new(content, targetWidth, targetHeight, targetWidth, targetHeight, padColor);

        var factor = Math.Min((double)targetWidth / content.Width, (double)targetHeight / content.Height);
        return new(content, Even(content.Width * factor), Even(content.Height * factor), targetWidth, targetHeight, padColor);
    }

    /// <summary>
    /// Fill the output: crop the largest window with the target's aspect out of <paramref name="content"/>, then scale it up or down.
    /// A custom offset is measured from the content box's top-left corner.
    /// </summary>
    public static CropPlan Fill(CropBox content, int targetWidth, int targetHeight, Anchor anchor, string padColor, int x = 0, int y = 0)
    {
        RequireEven(targetWidth, targetHeight);

        int w, h;
        if ((long)content.Width * targetHeight > (long)content.Height * targetWidth)
        {
            h = content.Height;
            w = (int)((long)h * targetWidth / targetHeight);
        }
        else
        {
            w = content.Width;
            h = (int)((long)w * targetHeight / targetWidth);
        }
        (w, h) = (Math.Max(2, w & ~1), Math.Max(2, h & ~1));

        (x, y) = anchor switch
        {
            Anchor.Center => ((content.Width - w) / 2, (content.Height - h) / 2),
            Anchor.TopLeft => (0, 0),
            _ => (x, y),
        };
        if (x < 0 || y < 0 || x + w > content.Width || y + h > content.Height)
            throw new ArgumentException($"A {w}x{h} crop at ({x},{y}) falls outside the {content.Width}x{content.Height} picture.");

        return Smart(new CropBox(content.X + x, content.Y + y, w, h), targetWidth, targetHeight, padColor);
    }

    /// <summary>Cut a user-chosen region out of the frame, then scale/pad it to the output size (black bars).</summary>
    public static CropPlan Region(VideoInfo source, int width, int height, Anchor anchor, int x, int y, int outWidth, int outHeight)
    {
        RequireEven(width, height);
        if (width > source.Width || height > source.Height)
            throw new ArgumentException($"A {width}x{height} region is larger than the source ({source.Width}x{source.Height}).");

        (x, y) = anchor switch
        {
            Anchor.Center => ((source.Width - width) / 2, (source.Height - height) / 2),
            Anchor.TopLeft => (0, 0),
            _ => (x, y),
        };
        if (x < 0 || y < 0 || x + width > source.Width || y + height > source.Height)
            throw new ArgumentException($"A {width}x{height} region at ({x},{y}) falls outside the {source.Width}x{source.Height} frame.");

        return Smart(new CropBox(x, y, width, height), outWidth, outHeight, "black");
    }

    private static void RequireEven(int w, int h)
    {
        if (w <= 0 || h <= 0 || w % 2 != 0 || h % 2 != 0)
            throw new ArgumentException("Width and height must be positive, even numbers.");
    }

    private static int Even(double v) => (int)Math.Round(v / 2.0) * 2;
}

public static class FilterBuilder
{
    public static string Build(CropPlan p)
    {
        var parts = new List<string> { $"crop={p.Crop.Width}:{p.Crop.Height}:{p.Crop.X}:{p.Crop.Y}" };
        if (p.NeedsScale) parts.Add($"scale={p.ScaleWidth}:{p.ScaleHeight}:flags=lanczos");
        if (p.NeedsPad) parts.Add($"pad={p.OutWidth}:{p.OutHeight}:(ow-iw)/2:(oh-ih)/2:color={p.PadColor}");
        parts.Add("setsar=1");
        return string.Join(',', parts);
    }
}

public static class AudioPolicy
{
    private static readonly HashSet<string> Mp4Friendly = new(StringComparer.OrdinalIgnoreCase) { "aac", "mp3", "ac3", "eac3", "alac" };

    public static string[] Args(string? codec) => codec switch
    {
        null => ["-an"],
        _ when Mp4Friendly.Contains(codec) => ["-c:a", "copy"],
        _ => ["-c:a", "aac", "-b:a", "192k"],
    };
}

public static class FfmpegArgsBuilder
{
    public static List<string> Build(string input, string output, string filter, VideoInfo source)
    {
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:1", "-y", "-i", input, "-map", "0:v:0" };
        if (source.AudioCodec is not null) a.AddRange(["-map", "0:a?"]);
        if (source.HasSubtitles) a.AddRange(["-map", "0:s?"]);
        a.AddRange(["-vf", filter, "-c:v", "libx264", "-crf", "14", "-preset", "slow", "-pix_fmt", "yuv420p"]);
        a.AddRange(AudioPolicy.Args(source.AudioCodec));
        if (source.HasSubtitles) a.AddRange(["-c:s", "mov_text"]);
        a.AddRange(["-movflags", "+faststart", output]);
        return a;
    }}

public static class ProgressParser
{
    private const string Key = "out_time_us=";

    public static bool TryGetElapsed(string line, out TimeSpan elapsed)
    {
        elapsed = default;
        if (!line.StartsWith(Key, StringComparison.Ordinal)) return false;
        if (!long.TryParse(line.AsSpan(Key.Length), out var us)) return false;
        elapsed = TimeSpan.FromTicks(us * 10);
        return true;
    }
}

public static class OutputNamer
{
    public static string Next(string source, Func<string, bool> exists)
    {
        var dir = Path.GetDirectoryName(source) ?? "";
        var stem = Path.GetFileNameWithoutExtension(source);
        var candidate = Path.Combine(dir, $"{stem}_cropped.mp4");
        for (var n = 2; exists(candidate); n++)
            candidate = Path.Combine(dir, $"{stem}_cropped_{n}.mp4");
        return candidate;
    }
}
