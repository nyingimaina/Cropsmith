using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace Cropsmith.Core;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args,
        Action<string>? onStdOutLine = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var so = new StringBuilder();
        var se = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is null) return; so.AppendLine(e.Data); onStdOutLine?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) se.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); p.WaitForExit(); } catch { } throw; }
        p.WaitForExit(); // flush async readers
        return new ProcessResult(p.ExitCode, so.ToString(), se.ToString());
    }
}

public static class ToolLocator
{
    public static string ManagedDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cropsmith", "bin");

    public static (string Ffmpeg, string Ffprobe)? TryLocate()
    {
        var dirs = new List<string> { ManagedDir };
        // Re-read PATH from the registry: a just-installed tool won't be in this process's copy.
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH", target) ?? "").Split(Path.PathSeparator));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"));

        var ffmpeg = FfmpegLocator.Find("ffmpeg.exe", dirs, File.Exists);
        var ffprobe = FfmpegLocator.Find("ffprobe.exe", dirs, File.Exists);
        return ffmpeg is not null && ffprobe is not null ? (ffmpeg, ffprobe) : null;
    }
}

public static class FfmpegInstaller
{
    private static readonly string[] Sources =
    [
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
    ];

    /// <summary>Downloads a static ffmpeg build into <see cref="ToolLocator.ManagedDir"/>. Reports (bytesDone, bytesTotal).</summary>
    public static async Task InstallAsync(IProgress<(long Done, long? Total)> progress, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Cropsmith/1.0");
        var zipPath = Path.Combine(Path.GetTempPath(), $"cropsmith-ffmpeg-{Guid.NewGuid():N}.zip");
        var errors = new List<string>();

        try
        {
            foreach (var url in Sources)
            {
                try
                {
                    using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();
                    var total = resp.Content.Headers.ContentLength;
                    await using (var net = await resp.Content.ReadAsStreamAsync(ct))
                    await using (var file = File.Create(zipPath))
                    {
                        var buf = new byte[81920];
                        long done = 0;
                        int n;
                        while ((n = await net.ReadAsync(buf, ct)) > 0)
                        {
                            await file.WriteAsync(buf.AsMemory(0, n), ct);
                            progress.Report((done += n, total));
                        }
                    }
                    if (ZipExtractor.ExtractBinaries(zipPath, ToolLocator.ManagedDir)) return;
                    errors.Add($"{url}: archive had no ffmpeg.exe");
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    errors.Add($"{url}: {ex.Message}");
                }
            }
            throw new InvalidOperationException("Could not download ffmpeg:\n" + string.Join('\n', errors));
        }
        finally
        {
            try { File.Delete(zipPath); } catch { }
        }
    }
}

public sealed class CropService(string ffmpeg, string ffprobe)
{
    public async Task<VideoInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        var r = await ProcessRunner.RunAsync(ffprobe,
            ["-v", "error", "-print_format", "json", "-show_streams", "-show_format", path], ct: ct);
        if (r.ExitCode != 0) throw new InvalidOperationException($"ffprobe could not read the file:\n{r.StdErr.Trim()}");
        return VideoProbe.Parse(r.StdOut);
    }

    /// <summary>As <see cref="ProbeAsync"/>, also reporting whether the source's subtitles are on by default.</summary>
    public async Task<VideoInfo> ProbeFullAsync(string path, CancellationToken ct = default)
    {
        var r = await ProcessRunner.RunAsync(ffprobe,
            ["-v", "error", "-print_format", "json", "-show_streams", "-show_format", path], ct: ct);
        if (r.ExitCode != 0) throw new InvalidOperationException($"ffprobe could not read the file:\n{r.StdErr.Trim()}");
        return VideoProbe.ParseFull(r.StdOut);
    }

    /// <summary>Finds the real picture area by sampling frames against both a white and a black background.</summary>
    public async Task<DetectedContent?> DetectContentAsync(string path, VideoInfo info, CancellationToken ct = default)
    {
        var fps = CropDetectParser.SampleFps(info.Duration);
        var white = await DetectAsync(path, $"fps={fps},negate,cropdetect=limit=20:round=2:reset=1:skip=0", ct);
        var black = await DetectAsync(path, $"fps={fps},cropdetect=limit=20:round=2:reset=1:skip=0", ct);
        return BackgroundResolver.Choose(white, black);
    }

    private async Task<CropBox?> DetectAsync(string path, string filter, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-i", path, "-vf", filter, "-an", "-sn", "-f", "null", "-"], ct: ct);
        return CropDetectParser.Union(r.StdErr);
    }

    public async Task CropAsync(string input, string output, CropPlan plan, VideoInfo info,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var args = FfmpegArgsBuilder.Build(input, output, FilterBuilder.Build(plan), info);
        ProcessResult r;
        try
        {
            r = await ProcessRunner.RunAsync(ffmpeg, args, line =>
            {
                if (progress is not null && info.Duration > TimeSpan.Zero && ProgressParser.TryGetElapsed(line, out var t))
                    progress.Report(Math.Clamp(t / info.Duration, 0, 1));
            }, ct);
        }
        catch (OperationCanceledException)
        {
            try { File.Delete(output); } catch { }
            throw;
        }

        if (r.ExitCode != 0)
        {
            try { File.Delete(output); } catch { }
            throw new InvalidOperationException($"ffmpeg failed (exit {r.ExitCode}):\n{r.StdErr.Trim()}");
        }
        progress?.Report(1);
    }

    /// <summary>As <see cref="CropAsync(string,string,CropPlan,VideoInfo,IProgress{double}?,CancellationToken)"/>, with a say over whether subtitles show by default.</summary>
    public async Task CropAsync(string input, string output, CropPlan plan, VideoInfo info, bool subtitlesOnByDefault,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var args = FfmpegArgsBuilder.Build(input, output, FilterBuilder.Build(plan), info);
        ProcessResult r;
        try
        {
            r = await ProcessRunner.RunAsync(ffmpeg, args, line =>
            {
                if (progress is not null && info.Duration > TimeSpan.Zero && ProgressParser.TryGetElapsed(line, out var t))
                    progress.Report(Math.Clamp(t / info.Duration, 0, 1));
            }, ct);
        }
        catch (OperationCanceledException)
        {
            try { File.Delete(output); } catch { }
            throw;
        }

        if (r.ExitCode != 0)
        {
            try { File.Delete(output); } catch { }
            throw new InvalidOperationException($"ffmpeg failed (exit {r.ExitCode}):\n{r.StdErr.Trim()}");
        }

        if (info.HasSubtitles && !subtitlesOnByDefault)
        {
            using var file = new FileStream(output, FileMode.Open, FileAccess.ReadWrite);
            Mp4Tracks.DisableSubtitleTracks(file);
        }
        progress?.Report(1);
    }
}


