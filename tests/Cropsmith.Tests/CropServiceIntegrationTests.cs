using Cropsmith.Core;
using Xunit;

namespace Cropsmith.Tests;

/// <summary>Drives the real ffmpeg on a synthetic clip; no-ops on machines without ffmpeg.</summary>
public class CropServiceIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cropsmith-it-" + Guid.NewGuid().ToString("N"));
    public CropServiceIntegrationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public async Task Detects_Crops_AndKeepsAudio()
    {
        var tools = ToolLocator.TryLocate();
        if (tools is null) return;
        var svc = new CropService(tools.Value.Ffmpeg, tools.Value.Ffprobe);

        var src = Path.Combine(_dir, "src.mp4");
        var made = await ProcessRunner.RunAsync(tools.Value.Ffmpeg, new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "color=white:s=640x360:d=2:r=10",
            "-f", "lavfi", "-i", "sine=d=2",
            "-vf", "drawbox=x=100:y=40:w=200:h=280:color=red:t=fill",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", src,
        });
        Assert.Equal(0, made.ExitCode);

        var info = await svc.ProbeAsync(src);
        Assert.Equal((640, 360), (info.Width, info.Height));
        Assert.Equal("aac", info.AudioCodec);

        var content = await svc.DetectContentAsync(src, info);
        Assert.NotNull(content);
        Assert.Equal("white", content!.PadColor);
        Assert.InRange(content.Box.Width, 196, 204);
        Assert.InRange(content.Box.Height, 276, 284);

        var plan = CropPlanner.Smart(content.Box, 100, 140, content.PadColor);
        var dst = Path.Combine(_dir, "dst.mp4");
        var ticks = new List<double>();
        await svc.CropAsync(src, dst, plan, info, new Progress<double>(ticks.Add));

        var outInfo = await svc.ProbeAsync(dst);
        Assert.Equal((100, 140), (outInfo.Width, outInfo.Height));
        Assert.NotNull(outInfo.AudioCodec);
        Assert.Equal(2, outInfo.Duration.TotalSeconds, 0.3);
    }

    [Theory]
    [InlineData(false, "0")]
    [InlineData(true, "1")]
    public async Task Subtitles_KeepTheTrack_AndHonourTheDefaultFlag(bool onByDefault, string expectedDefault)
    {
        var tools = ToolLocator.TryLocate();
        if (tools is null) return;
        var svc = new CropService(tools.Value.Ffmpeg, tools.Value.Ffprobe);

        var srt = Path.Combine(_dir, "s.srt");
        await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:01,000\nhello\n");
        var src = Path.Combine(_dir, "subs.mp4");
        var made = await ProcessRunner.RunAsync(tools.Value.Ffmpeg, new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "color=white:s=320x240:d=2:r=10", "-i", srt,
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:s", "mov_text", src,
        });
        Assert.Equal(0, made.ExitCode);

        var info = await svc.ProbeAsync(src);
        Assert.True(info.HasSubtitles);
        var plan = CropPlanner.Region(info, 160, 120, Anchor.Center, 0, 0, 160, 120);
        var dst = Path.Combine(_dir, "subs_out.mp4");
        await svc.CropAsync(src, dst, plan, info, onByDefault);

        Assert.True((await svc.ProbeAsync(dst)).HasSubtitles);
        var probe = await ProcessRunner.RunAsync(tools.Value.Ffprobe, new[]
        {
            "-v", "error", "-select_streams", "s", "-show_entries", "stream_disposition=default", "-of", "csv=p=0", dst,
        });
        Assert.Equal(expectedDefault, probe.StdOut.Trim());
    }

    [Fact]
    public async Task CropAsync_Throws_OnBadInput()
    {
        var tools = ToolLocator.TryLocate();
        if (tools is null) return;
        var svc = new CropService(tools.Value.Ffmpeg, tools.Value.Ffprobe);
        var info = new VideoInfo(10, 10, TimeSpan.FromSeconds(1), null, false);
        var plan = CropPlanner.Exact(info, 10, 10, Anchor.TopLeft);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.CropAsync(Path.Combine(_dir, "missing.mp4"), Path.Combine(_dir, "o.mp4"), plan, info));
    }
}

public class CropServiceCancellationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cropsmith-cx-" + Guid.NewGuid().ToString("N"));
    public CropServiceCancellationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public async Task CancelledCrop_ThrowsCancelled_AndLeavesNoPartialFile()
    {
        var tools = ToolLocator.TryLocate();
        if (tools is null) return;
        var svc = new CropService(tools.Value.Ffmpeg, tools.Value.Ffprobe);

        var src = Path.Combine(_dir, "long.mp4");
        var made = await ProcessRunner.RunAsync(tools.Value.Ffmpeg, new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc=s=1280x720:d=120:r=30",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", src,
        });
        Assert.Equal(0, made.ExitCode);

        var info = await svc.ProbeAsync(src);
        var plan = CropPlanner.Exact(info, 640, 360, Anchor.Center);
        var dst = Path.Combine(_dir, "out.mp4");
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(600);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.CropAsync(src, dst, plan, info, null, cts.Token));
        Assert.False(File.Exists(dst));
    }
}
