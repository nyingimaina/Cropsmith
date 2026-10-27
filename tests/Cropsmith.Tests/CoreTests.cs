using Cropsmith.Core;
using Xunit;

namespace Cropsmith.Tests;

public class CropDetectParserTests
{
    [Fact]
    public void Union_ReturnsNull_WhenNoCropLines() =>
        Assert.Null(CropDetectParser.Union("frame=1 fps=0"));

    [Fact]
    public void Union_ParsesSingleLine_UsingRawEdges()
    {
        var box = CropDetectParser.Union("[Parsed_cropdetect_1 @ 0x1] x1:40 x2:617 y1:13 y2:1046 w:578 h:1032 x:40 y:14 pts:9000 t:0.1");
        Assert.Equal(new CropBox(40, 13, 578, 1034), box);
    }

    [Fact]
    public void Union_GrowsToCoverEveryFrame()
    {
        var output = "x1:40 x2:617 y1:30 y2:1046 w:1 h:1\nx1:40 x2:616 y1:13 y2:1040 w:1 h:1\nnoise";
        Assert.Equal(new CropBox(40, 13, 578, 1034), CropDetectParser.Union(output));
    }

    [Fact]
    public void SampleFps_CapsAtOnePerSecond_ForShortVideos() =>
        Assert.Equal("1", CropDetectParser.SampleFps(TimeSpan.FromSeconds(60)));

    [Fact]
    public void SampleFps_SlowsDown_ForLongVideos() =>
        Assert.Equal("0.0333333", CropDetectParser.SampleFps(TimeSpan.FromSeconds(3600)));

    [Fact]
    public void SampleFps_HandlesZeroDuration() =>
        Assert.Equal("1", CropDetectParser.SampleFps(TimeSpan.Zero));
}

public class BackgroundResolverTests
{
    [Fact]
    public void PicksWhite_WhenNegatedDetectionIsTighter()
    {
        var r = BackgroundResolver.Choose(new CropBox(40, 13, 578, 1034), new CropBox(0, 0, 1920, 1080));
        Assert.Equal(new CropBox(40, 13, 578, 1034), r!.Box);
        Assert.Equal("white", r.PadColor);
    }

    [Fact]
    public void PicksBlack_WhenPlainDetectionIsTighter()
    {
        var r = BackgroundResolver.Choose(new CropBox(0, 0, 1920, 1080), new CropBox(100, 0, 500, 1080));
        Assert.Equal("black", r!.PadColor);
    }

    [Fact]
    public void ReturnsNull_WhenNothingDetected() =>
        Assert.Null(BackgroundResolver.Choose(null, null));

    [Fact]
    public void UsesWhichever_WhenOnlyOneDetected() =>
        Assert.Equal("black", BackgroundResolver.Choose(null, new CropBox(1, 1, 10, 10))!.PadColor);
}

public class CropPlannerTests
{
    private static readonly VideoInfo Src = new(1920, 1080, TimeSpan.FromSeconds(77), "aac", true);

    [Fact]
    public void Smart_ScalesExactly_WhenAspectMatchesWithinOnePercent()
    {
        var plan = CropPlanner.Smart(new CropBox(40, 13, 578, 1034), 768, 1376, "white");
        Assert.Equal((768, 1376), (plan.ScaleWidth, plan.ScaleHeight));
        Assert.False(plan.NeedsPad);
        Assert.Equal("crop=578:1034:40:13,scale=768:1376:flags=lanczos,setsar=1", FilterBuilder.Build(plan));
    }

    [Fact]
    public void Smart_PadsToTarget_WhenAspectDiffers()
    {
        var plan = CropPlanner.Smart(new CropBox(0, 0, 1000, 500), 800, 800, "black");
        Assert.Equal((800, 400), (plan.ScaleWidth, plan.ScaleHeight));
        Assert.True(plan.NeedsPad);
        Assert.Equal("crop=1000:500:0:0,scale=800:400:flags=lanczos,pad=800:800:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1", FilterBuilder.Build(plan));
    }

    [Fact]
    public void Smart_SkipsScale_WhenContentAlreadyTargetSize()
    {
        var plan = CropPlanner.Smart(new CropBox(10, 20, 768, 1376), 768, 1376, "white");
        Assert.Equal("crop=768:1376:10:20,setsar=1", FilterBuilder.Build(plan));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(101, 100)]
    [InlineData(100, -2)]
    public void Smart_RejectsBadTargets(int w, int h) =>
        Assert.Throws<ArgumentException>(() => CropPlanner.Smart(new CropBox(0, 0, 10, 10), w, h, "white"));

    [Fact]
    public void Exact_CentersTheWindow()
    {
        var plan = CropPlanner.Exact(Src, 800, 600, Anchor.Center);
        Assert.Equal(new CropBox(560, 240, 800, 600), plan.Crop);
        Assert.Equal("crop=800:600:560:240,setsar=1", FilterBuilder.Build(plan));
    }

    [Fact]
    public void Exact_TopLeft() =>
        Assert.Equal(new CropBox(0, 0, 800, 600), CropPlanner.Exact(Src, 800, 600, Anchor.TopLeft).Crop);

    [Fact]
    public void Exact_Custom_UsesGivenOffset() =>
        Assert.Equal(new CropBox(40, 14, 578, 1034 - 2), CropPlanner.Exact(Src, 578, 1032, Anchor.Custom, 40, 14).Crop);

    [Fact]
    public void Exact_RejectsTargetLargerThanSource() =>
        Assert.Throws<ArgumentException>(() => CropPlanner.Exact(Src, 2000, 600, Anchor.Center));

    [Fact]
    public void Exact_RejectsCustomOffsetThatFallsOutside() =>
        Assert.Throws<ArgumentException>(() => CropPlanner.Exact(Src, 800, 600, Anchor.Custom, 1200, 0));

    [Fact]
    public void Exact_RejectsOddSizes() =>
        Assert.Throws<ArgumentException>(() => CropPlanner.Exact(Src, 801, 600, Anchor.Center));
}

public class AudioPolicyTests
{
    [Theory]
    [InlineData("aac")]
    [InlineData("mp3")]
    [InlineData("ac3")]
    public void CopiesMp4Friendly(string codec) =>
        Assert.Equal(new[] { "-c:a", "copy" }, AudioPolicy.Args(codec));

    [Fact]
    public void ReencodesOthers() =>
        Assert.Equal(new[] { "-c:a", "aac", "-b:a", "192k" }, AudioPolicy.Args("opus"));

    [Fact]
    public void DropsAudio_WhenNone() =>
        Assert.Equal(new[] { "-an" }, AudioPolicy.Args(null));
}

public class FfmpegArgsBuilderTests
{
    [Fact]
    public void Build_KeepsAudioAndSubtitles_AndReportsProgress()
    {
        var a = FfmpegArgsBuilder.Build("in.mp4", "out.mp4", "crop=1:1:0:0", new VideoInfo(10, 10, TimeSpan.FromSeconds(1), "aac", true));
        Assert.Contains("crop=1:1:0:0", a);
        Assert.Equal(new[] { "-map", "0:a?" }, a.SkipWhile(x => x != "-map").Skip(2).Take(2));
        Assert.Contains("0:s?", a);
        Assert.Contains("mov_text", a);
        Assert.Contains("pipe:1", a);
        Assert.Equal("out.mp4", a[^1]);
    }

    [Fact]
    public void Build_OmitsSubtitles_WhenSourceHasNone()
    {
        var a = FfmpegArgsBuilder.Build("in.mp4", "out.mp4", "f", new VideoInfo(10, 10, TimeSpan.FromSeconds(1), "aac", false));
        Assert.DoesNotContain("0:s?", a);
        Assert.DoesNotContain("mov_text", a);
    }

    [Fact]
    public void Build_NoAudioMap_WhenSourceSilent()
    {
        var a = FfmpegArgsBuilder.Build("in.mp4", "out.mp4", "f", new VideoInfo(10, 10, TimeSpan.FromSeconds(1), null, false));
        Assert.DoesNotContain("0:a?", a);
        Assert.Contains("-an", a);
    }
}

public class ProgressParserTests
{
    [Fact]
    public void ParsesMicroseconds()
    {
        Assert.True(ProgressParser.TryGetElapsed("out_time_us=12500000", out var t));
        Assert.Equal(TimeSpan.FromSeconds(12.5), t);
    }

    [Theory]
    [InlineData("out_time_us=N/A")]
    [InlineData("frame=10")]
    [InlineData("")]
    public void IgnoresOtherLines(string line) =>
        Assert.False(ProgressParser.TryGetElapsed(line, out _));
}

public class VideoProbeTests
{
    private const string Json = """
    {"streams":[
      {"codec_type":"video","width":1920,"height":1080},
      {"codec_type":"audio","codec_name":"aac"},
      {"codec_type":"subtitle","codec_name":"mov_text"}],
     "format":{"duration":"77.484989"}}
    """;

    [Fact]
    public void Parse_ReadsDimensionsDurationAndStreams()
    {
        var v = VideoProbe.Parse(Json);
        Assert.Equal(1920, v.Width);
        Assert.Equal(1080, v.Height);
        Assert.Equal("aac", v.AudioCodec);
        Assert.True(v.HasSubtitles);
        Assert.Equal(77.484989, v.Duration.TotalSeconds, 3);
    }

    [Fact]
    public void Parse_Throws_WhenNoVideoStream() =>
        Assert.Throws<InvalidDataException>(() => VideoProbe.Parse("""{"streams":[{"codec_type":"audio"}],"format":{}}"""));
}

public class FfmpegLocatorTests
{
    [Fact]
    public void Find_ReturnsFirstDirectoryThatHasTheExe()
    {
        var hit = FfmpegLocator.Find("ffmpeg.exe", new[] { @"C:\a", @"C:\b", @"C:\c" }, p => p == @"C:\b\ffmpeg.exe" || p == @"C:\c\ffmpeg.exe");
        Assert.Equal(@"C:\b\ffmpeg.exe", hit);
    }

    [Fact]
    public void Find_SkipsBlankEntries_AndReturnsNullWhenMissing() =>
        Assert.Null(FfmpegLocator.Find("ffmpeg.exe", new[] { "", "  " }, _ => true));
}

public class ZipExtractorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cropsmith-" + Guid.NewGuid().ToString("N"));
    public ZipExtractorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void ExtractBinaries_PullsOnlyFfmpegAndFfprobe()
    {
        var zip = Path.Combine(_dir, "b.zip");
        using (var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var n in new[] { "ffmpeg-7/bin/ffmpeg.exe", "ffmpeg-7/bin/ffprobe.exe", "ffmpeg-7/bin/ffplay.exe", "ffmpeg-7/doc/readme.txt" })
                using (var w = new StreamWriter(z.CreateEntry(n).Open())) w.Write(n);
        }
        var dest = Path.Combine(_dir, "out");
        Assert.True(ZipExtractor.ExtractBinaries(zip, dest));
        Assert.Equal(new[] { "ffmpeg.exe", "ffprobe.exe" }, Directory.GetFiles(dest).Select(Path.GetFileName).OrderBy(x => x));
    }

    [Fact]
    public void ExtractBinaries_ReturnsFalse_WhenArchiveLacksFfmpeg()
    {
        var zip = Path.Combine(_dir, "e.zip");
        using (System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create)) { }
        Assert.False(ZipExtractor.ExtractBinaries(zip, Path.Combine(_dir, "o2")));
    }
}

public class OutputNamerTests
{
    [Fact]
    public void Next_AppendsCropped_NextToSource() =>
        Assert.Equal(@"C:\v\clip_cropped.mp4", OutputNamer.Next(@"C:\v\clip.mov", _ => false));

    [Fact]
    public void Next_NumbersWhenTaken() =>
        Assert.Equal(@"C:\v\clip_cropped_3.mp4",
            OutputNamer.Next(@"C:\v\clip.mp4", p => p is @"C:\v\clip_cropped.mp4" or @"C:\v\clip_cropped_2.mp4"));
}

public class PathInputTests
{
    [Theory]
    [InlineData("\"C:\\v\\a b.mp4\"", @"C:\v\a b.mp4")]
    [InlineData("  'C:\\v\\a.mp4'  ", @"C:\v\a.mp4")]
    [InlineData(@"C:\v\a.mp4", @"C:\v\a.mp4")]
    public void Clean_StripsQuotesAndWhitespace(string raw, string expected) =>
        Assert.Equal(expected, PathInput.Clean(raw));

    [Theory]
    [InlineData(@"C:\a\x.MP4", true)]
    [InlineData(@"C:\a\x.mkv", true)]
    [InlineData(@"C:\a\x.txt", false)]
    public void IsVideo_ChecksExtension(string path, bool expected) =>
        Assert.Equal(expected, PathInput.IsVideo(path));
}
