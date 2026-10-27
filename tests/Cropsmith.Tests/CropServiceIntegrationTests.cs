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
