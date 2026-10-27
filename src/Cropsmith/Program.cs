using Cropsmith.Core;
using Spectre.Console;

namespace Cropsmith;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.TreatControlCAsInput = true; // Ctrl+C arrives as a key so every screen can handle it

        try
        {
            Banner();
            var tools = await EnsureToolsAsync();
            if (tools is null) return 1;
            var svc = new CropService(tools.Value.Ffmpeg, tools.Value.Ffprobe);

            var first = args.Length > 0 ? PathInput.Clean(args[0]) : null;
            while (true)
            {
                try
                {
                    var source = first is not null && File.Exists(first) ? first : PickSource();
                    first = null;
                    await RunOneAsync(svc, source);
                    if (!Prompts.Confirm("\nCrop another video?", defaultYes: false)) break;
                }
                catch (BackException)
                {
                    // Esc on the first screen of a job: offer to leave, otherwise start over.
                    if (AskQuit()) break;
                }
            }
            Goodbye();
            return 0;
        }
        catch (QuitException)
        {
            Goodbye();
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("\n[red bold]Something went wrong[/]");
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenPaths);
            return 1;
        }
    }

    private static bool AskQuit()
    {
        try { return Prompts.Confirm("Quit Cropsmith?", defaultYes: false); }
        catch (BackException) { return false; }
    }

    private static void Goodbye() => AnsiConsole.MarkupLine("\n[grey]Happy cropping.[/]");

    private static void Banner()
    {
        AnsiConsole.Write(new FigletText("Cropsmith").Color(Color.Orange1));
        AnsiConsole.Write(new Rule("[grey]forge your video to the exact frame · picture and sound stay intact[/]").RuleStyle("grey").LeftJustified());
        AnsiConsole.MarkupLine("[grey]Keys: [bold]Enter[/] confirm · [bold]Esc[/] go back / cancel · [bold]Ctrl+C[/] or [bold]Q[/] quit[/]\n");
    }

    // ── dependencies ────────────────────────────────────────────────────────
    private static async Task<(string Ffmpeg, string Ffprobe)?> EnsureToolsAsync()
    {
        var tools = ToolLocator.TryLocate();
        if (tools is not null) return tools;

        AnsiConsole.MarkupLine("[yellow]Cropsmith needs [bold]ffmpeg[/], which isn't installed on this machine.[/]");
        AnsiConsole.MarkupLine($"[grey]It will be downloaded once (~100 MB) into[/] [blue]{Markup.Escape(ToolLocator.ManagedDir)}[/]");
        bool yes;
        try { yes = Prompts.Confirm("Download it now?"); }
        catch (BackException) { yes = false; }
        if (!yes)
        {
            AnsiConsole.MarkupLine("[red]Can't continue without ffmpeg.[/]");
            return null;
        }

        using var job = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watcher = Prompts.Watch(job, stop.Token);
        try
        {
            await AnsiConsole.Progress()
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new DownloadedColumn(), new TransferSpeedColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("Downloading ffmpeg [grey](Esc to cancel)[/]", maxValue: 100_000_000);
                    await FfmpegInstaller.InstallAsync(new Progress<(long Done, long? Total)>(p =>
                    {
                        if (p.Total is { } t) task.MaxValue = t;
                        task.Value = p.Done;
                    }), job.Token);
                    task.Value = task.MaxValue;
                });
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Download cancelled.[/]");
            return null;
        }
        finally
        {
            stop.Cancel();
            await watcher;
        }

        tools = ToolLocator.TryLocate();
        if (tools is null) throw new InvalidOperationException("ffmpeg was downloaded but could not be found afterwards.");
        AnsiConsole.MarkupLine("[green]✔ ffmpeg ready[/]\n");
        return tools;
    }

    // ── choosing a file ─────────────────────────────────────────────────────
    private static string PickSource()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        while (true)
        {
            var how = Prompts.Select("Which video do you want to crop?",
                new[] { "Browse a folder", "Paste or drag in a file path" }, h => h);
            try
            {
                if (how.StartsWith("Paste")) return AskFilePath();

                while (true)
                {
                    folder = Prompts.Ask<string>("Folder to look in:", folder, s =>
                    {
                        var c = PathInput.Clean(s);
                        return Directory.Exists(c) ? (c, null) : (null, "That folder doesn't exist.");
                    })!;
                    var videos = new DirectoryInfo(folder).EnumerateFiles().Where(f => PathInput.IsVideo(f.Name))
                        .OrderByDescending(f => f.LastWriteTime).ToList();
                    if (videos.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[yellow]No videos in that folder — try another.[/]");
                        continue;
                    }
                    try
                    {
                        return Prompts.Select($"Pick a video [grey]({videos.Count} found, newest first)[/]", videos,
                            f => $"{Markup.Escape(f.Name)}  [grey]{Size(f.Length)} · {f.LastWriteTime:yyyy-MM-dd HH:mm}[/]").FullName;
                    }
                    catch (BackException) { /* back to the folder prompt */ }
                }
            }
            catch (BackException) { /* back to "how" */ }
        }
    }

    private static string AskFilePath() =>
        Prompts.Ask<string>("Path to the [orange1]video file[/]:", "", s =>
        {
            var c = PathInput.Clean(s);
            return File.Exists(c) ? (c, null) : (null, "That file doesn't exist.");
        })!;

    // ── one crop job: a small step machine so Esc always steps back ─────────
    private enum Step { Mode, Size, Anchor, OutSize, Subtitles, Confirm }

    private static async Task RunOneAsync(CropService svc, string source)
    {
        var info = await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync("Reading video…", _ => svc.ProbeAsync(source));
        ShowInfo(source, info);

        var mode = CropMode.Smart;
        DetectedContent? content = null;
        int? w = null, h = null, ow = null, oh = null;
        var subtitlesOn = true;
        var anchor = Anchor.Center;
        int x = 0, y = 0;
        CropPlan? plan = null;
        var step = Step.Mode;

        while (true)
        {
            switch (step)
            {
                case Step.Mode:
                    mode = Prompts.Select("How should it be cropped?", new[] { CropMode.Smart, CropMode.Exact },
                        m => m == CropMode.Smart
                            ? "[orange1]Smart[/]  – find the picture, trim the empty space, fit it to my width × height"
                            : "[orange1]Exact[/]  – cut a precise region out of the frame, then fit it to my output size",
                        initial: mode == CropMode.Smart ? 0 : 1);
                    if (mode == CropMode.Smart && content is null) content = await DetectAsync(svc, source, info);
                    step = Step.Size;
                    break;

                case Step.Size:
                    try
                    {
                        var defW = w ?? ((mode == CropMode.Smart ? content!.Box.Width : info.Width) & ~1);
                        var defH = h ?? ((mode == CropMode.Smart ? content!.Box.Height : info.Height) & ~1);
                        var what = mode == CropMode.Smart ? "Output" : "Region";
                        w = AskEven($"{what} [orange1]width[/]  (px):", defW);
                        h = AskEven($"{what} [orange1]height[/] (px):", defH);
                    }
                    catch (BackException) { step = Step.Mode; break; }
                    if (mode == CropMode.Exact) { step = Step.Anchor; break; }
                    step = TryPlan(() => CropPlanner.Smart(content!.Box, w!.Value, h!.Value, content.PadColor), out plan)
                        ? AfterPlan(info) : Step.Size;
                    break;

                case Step.Anchor:
                    try
                    {
                        anchor = Prompts.Select("Where should the region sit?",
                            new[] { Anchor.Center, Anchor.TopLeft, Anchor.Custom },
                            a => a switch { Anchor.Center => "Centered", Anchor.TopLeft => "Top-left corner", _ => "Custom X, Y" },
                            initial: (int)anchor);
                        if (anchor == Anchor.Custom)
                        {
                            x = AskInt("X offset:", x);
                            y = AskInt("Y offset:", y);
                        }
                    }
                    catch (BackException) { step = Step.Size; break; }
                    step = Step.OutSize;
                    break;

                case Step.OutSize:
                    try
                    {
                        ow = AskEven("Output [orange1]width[/]  (px):", ow ?? w!.Value);
                        oh = AskEven("Output [orange1]height[/] (px):", oh ?? h!.Value);
                    }
                    catch (BackException) { step = Step.Anchor; break; }
                    step = TryPlan(() => CropPlanner.Region(info, w!.Value, h!.Value, anchor, x, y, ow!.Value, oh!.Value), out plan)
                        ? AfterPlan(info) : Step.Size;
                    break;

                case Step.Subtitles:
                    try
                    {
                        subtitlesOn = !Prompts.Confirm("This video has subtitles. Turn them [orange1]off by default[/]? [grey](they stay in the file; switch them on in your player)[/]",
                            defaultYes: !subtitlesOn);
                    }
                    catch (BackException) { step = mode == CropMode.Exact ? Step.OutSize : Step.Size; break; }
                    step = Step.Confirm;
                    break;

                case Step.Confirm:
                    var output = OutputNamer.Next(source, File.Exists);
                    ShowPlan(plan!, info, output);
                    if (info.HasSubtitles)
                        AnsiConsole.MarkupLine($"[grey]Subtitles:[/] {(subtitlesOn ? "on by default" : "off by default (still in the file)")}");
                    bool go;
                    try { go = Prompts.Confirm("Start cropping?"); }
                    catch (BackException) { step = info.HasSubtitles ? Step.Subtitles : mode == CropMode.Exact ? Step.OutSize : Step.Size; break; }
                    if (!go) { step = Step.Size; break; }
                    await CropWithProgressAsync(svc, source, output, plan!, info, subtitlesOn);
                    return;
            }
        }
    }

    private static Step AfterPlan(VideoInfo info) => info.HasSubtitles ? Step.Subtitles : Step.Confirm;

    private static async Task<DetectedContent> DetectAsync(CropService svc, string source, VideoInfo info)
    {
        var content = await AnsiConsole.Status().Spinner(Spinner.Known.Dots)
            .StartAsync("Looking for the picture inside the frame…", _ => svc.DetectContentAsync(source, info));
        if (content is null || content.Box.Area >= (long)info.Width * info.Height * 99 / 100)
        {
            AnsiConsole.MarkupLine("[yellow]No empty border found — the picture fills the whole frame.[/]");
            return new DetectedContent(new CropBox(0, 0, info.Width, info.Height), content?.PadColor ?? "black");
        }
        var b = content.Box;
        AnsiConsole.MarkupLine($"[green]✔ Picture found:[/] {b.Width}×{b.Height} at ({b.X}, {b.Y})  [grey]on a {content.PadColor} background[/]");
        return content;
    }

    private static bool TryPlan(Func<CropPlan> build, out CropPlan? plan)
    {
        try { plan = build(); return true; }
        catch (ArgumentException ex)
        {
            AnsiConsole.MarkupLine($"[red]✘ {Markup.Escape(ex.Message)}[/] Let's try again.\n");
            plan = null;
            return false;
        }
    }

    private static async Task CropWithProgressAsync(CropService svc, string source, string output, CropPlan plan, VideoInfo info, bool subtitlesOn)
    {
        using var job = new CancellationTokenSource();
        using var stop = new CancellationTokenSource();
        var watcher = Prompts.Watch(job, stop.Token);
        try
        {
            await AnsiConsole.Progress()
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new ElapsedTimeColumn(), new RemainingTimeColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("Cropping [grey](Esc to cancel)[/]", maxValue: 1);
                    await svc.CropAsync(source, output, plan, info, subtitlesOn, new Progress<double>(v => task.Value = v), job.Token);
                });
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Cropping cancelled — nothing was saved.[/]");
            return;
        }
        finally
        {
            stop.Cancel();
            await watcher;
        }

        AnsiConsole.Write(new Panel(new Rows(
                new Markup("[green bold]✔ Done[/]"),
                new Markup($"[grey]File:[/]  [blue]{Markup.Escape(output)}[/]"),
                new Markup($"[grey]Size:[/]  {plan.OutWidth}×{plan.OutHeight}  ·  {Size(new FileInfo(output).Length)}  ·  audio {(info.AudioCodec is null ? "none" : "kept")}")))
            .Border(BoxBorder.Rounded).BorderColor(Color.Green).Padding(1, 0));

        try
        {
            if (Prompts.Confirm("Show it in Explorer?", defaultYes: false))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{output}\"");
        }
        catch (BackException) { }
    }

    private static int AskEven(string prompt, int def) =>
        Prompts.Ask(prompt, def.ToString(), s =>
            int.TryParse(s.Trim(), out var v) && v > 0 && v % 2 == 0 ? (v, null) : (0, "Use a positive, even whole number."));

    private static int AskInt(string prompt, int def) =>
        Prompts.Ask(prompt, def.ToString(), s =>
            int.TryParse(s.Trim(), out var v) && v >= 0 ? (v, null) : (0, "Use a whole number, 0 or more."));

    private static void ShowInfo(string path, VideoInfo i)
    {
        var t = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).HideHeaders().AddColumn("k").AddColumn("v");
        t.AddRow("[grey]File[/]", Markup.Escape(Path.GetFileName(path)));
        t.AddRow("[grey]Frame[/]", $"{i.Width}×{i.Height}");
        t.AddRow("[grey]Length[/]", i.Duration.ToString(@"h\:mm\:ss"));
        t.AddRow("[grey]Audio[/]", i.AudioCodec ?? "none");
        if (i.HasSubtitles) t.AddRow("[grey]Subtitles[/]", "yes");
        AnsiConsole.Write(t);
    }

    private static void ShowPlan(CropPlan p, VideoInfo i, string output)
    {
        var t = new Table().Border(TableBorder.Rounded).BorderColor(Color.Orange1).Title("[orange1]The plan[/]").HideHeaders().AddColumn("k").AddColumn("v");
        t.AddRow("[grey]Cut out[/]", $"{p.Crop.Width}×{p.Crop.Height} at ({p.Crop.X}, {p.Crop.Y})  [grey]of {i.Width}×{i.Height}[/]");
        if (p.NeedsScale) t.AddRow("[grey]Resize to[/]", $"{p.ScaleWidth}×{p.ScaleHeight}");
        if (p.NeedsPad) t.AddRow("[grey]Pad with[/]", $"{p.PadColor} up to {p.OutWidth}×{p.OutHeight}");
        t.AddRow("[grey]Output[/]", $"{p.OutWidth}×{p.OutHeight}");
        t.AddRow("[grey]Save as[/]", Markup.Escape(output));
        AnsiConsole.Write(t);
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };
}

