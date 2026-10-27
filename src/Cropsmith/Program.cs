using Cropsmith.Core;
using Spectre.Console;

namespace Cropsmith;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            Banner();
            var tools = await EnsureToolsAsync(cts.Token);
            if (tools is null) return 1;
            var svc = new CropService(tools.Value.Ffmpeg, tools.Value.Ffprobe);

            var first = args.Length > 0 ? PathInput.Clean(args[0]) : null;
            do
            {
                var source = first is not null && File.Exists(first) ? first : PickSource();
                first = null;
                await RunOneAsync(svc, source, cts.Token);
            }
            while (AnsiConsole.Confirm("\n[grey]Crop another video?[/]", defaultValue: false));

            AnsiConsole.MarkupLine("\n[grey]Happy cropping.[/]");
            return 0;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("\n[yellow]Cancelled.[/]");
            return 130;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("\n[red bold]Something went wrong[/]");
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenPaths);
            return 1;
        }
    }

    private static void Banner()
    {
        AnsiConsole.Write(new FigletText("Cropsmith").Color(Color.Orange1));
        AnsiConsole.Write(new Rule("[grey]forge your video to the exact frame · picture and sound stay intact[/]").RuleStyle("grey").LeftJustified());
        AnsiConsole.WriteLine();
    }

    // ── dependencies ────────────────────────────────────────────────────────
    private static async Task<(string Ffmpeg, string Ffprobe)?> EnsureToolsAsync(CancellationToken ct)
    {
        var tools = ToolLocator.TryLocate();
        if (tools is not null) return tools;

        AnsiConsole.MarkupLine("[yellow]Cropsmith needs [bold]ffmpeg[/], which isn't installed on this machine.[/]");
        AnsiConsole.MarkupLine($"[grey]It will be downloaded once (~100 MB) into[/] [blue]{Markup.Escape(ToolLocator.ManagedDir)}[/]");
        if (!AnsiConsole.Confirm("Download it now?"))
        {
            AnsiConsole.MarkupLine("[red]Can't continue without ffmpeg.[/]");
            return null;
        }

        await AnsiConsole.Progress()
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new DownloadedColumn(), new TransferSpeedColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("Downloading ffmpeg", maxValue: 100_000_000);
                await FfmpegInstaller.InstallAsync(new Progress<(long Done, long? Total)>(p =>
                {
                    if (p.Total is { } t) task.MaxValue = t;
                    task.Value = p.Done;
                }), ct);
                task.Value = task.MaxValue;
            });

        tools = ToolLocator.TryLocate();
        if (tools is null) throw new InvalidOperationException("ffmpeg was downloaded but could not be found afterwards.");
        AnsiConsole.MarkupLine("[green]✔ ffmpeg ready[/]\n");
        return tools;
    }

    // ── choosing a file ─────────────────────────────────────────────────────
    private static string PickSource()
    {
        var how = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("[bold]Which video do you want to crop?[/]")
            .AddChoices("Browse a folder", "Paste or drag in a file path"));

        if (how.StartsWith("Paste"))
        {
            return PathInput.Clean(AnsiConsole.Prompt(new TextPrompt<string>("Path to the [orange1]video file[/]:")
                .Validate(p => File.Exists(PathInput.Clean(p)) ? ValidationResult.Success() : ValidationResult.Error("[red]That file doesn't exist.[/]"))));
        }

        var defaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        while (true)
        {
            var folder = PathInput.Clean(AnsiConsole.Prompt(new TextPrompt<string>("Folder to look in:")
                .DefaultValue(defaultFolder)
                .Validate(p => Directory.Exists(PathInput.Clean(p)) ? ValidationResult.Success() : ValidationResult.Error("[red]That folder doesn't exist.[/]"))));
            var videos = new DirectoryInfo(folder).EnumerateFiles().Where(f => PathInput.IsVideo(f.Name))
                .OrderByDescending(f => f.LastWriteTime).ToList();
            if (videos.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No videos in that folder — try another.[/]");
                defaultFolder = folder;
                continue;
            }
            var pick = AnsiConsole.Prompt(new SelectionPrompt<FileInfo>()
                .Title($"[bold]Pick a video[/] [grey]({videos.Count} found, newest first)[/]")
                .PageSize(12).MoreChoicesText("[grey](↑↓ for more)[/]")
                .UseConverter(f => $"{Markup.Escape(f.Name)}  [grey]{Size(f.Length)} · {f.LastWriteTime:yyyy-MM-dd HH:mm}[/]")
                .AddChoices(videos));
            return pick.FullName;
        }
    }

    // ── one crop job ────────────────────────────────────────────────────────
    private static async Task RunOneAsync(CropService svc, string source, CancellationToken ct)
    {
        var info = await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync("Reading video…", _ => svc.ProbeAsync(source, ct));
        ShowInfo(source, info);

        var mode = AnsiConsole.Prompt(new SelectionPrompt<CropMode>()
            .Title("[bold]How should it be cropped?[/]")
            .UseConverter(m => m == CropMode.Smart
                ? "[orange1]Smart[/]  – find the picture, trim the empty space, fit it to my width × height"
                : "[orange1]Exact[/]  – cut a precise width × height window out of the frame")
            .AddChoices(CropMode.Smart, CropMode.Exact));

        DetectedContent? content = null;
        if (mode == CropMode.Smart)
        {
            content = await AnsiConsole.Status().Spinner(Spinner.Known.Dots)
                .StartAsync("Looking for the picture inside the frame…", _ => svc.DetectContentAsync(source, info, ct));
            if (content is null || content.Box.Area >= (long)info.Width * info.Height * 99 / 100)
            {
                AnsiConsole.MarkupLine("[yellow]No empty border found — the picture fills the whole frame.[/]");
                content = new DetectedContent(new CropBox(0, 0, info.Width, info.Height), content?.PadColor ?? "black");
            }
            else
            {
                var b = content.Box;
                AnsiConsole.MarkupLine($"[green]✔ Picture found:[/] {b.Width}×{b.Height} at ({b.X}, {b.Y})  [grey]on a {content.PadColor} background[/]");
            }
        }

        CropPlan plan;
        while (true)
        {
            var defW = (content?.Box.Width ?? info.Width) & ~1;
            var defH = (content?.Box.Height ?? info.Height) & ~1;
            var w = AskEven("Output [orange1]width[/]  (px):", defW);
            var h = AskEven("Output [orange1]height[/] (px):", defH);
            try
            {
                if (mode == CropMode.Smart) plan = CropPlanner.Smart(content!.Box, w, h, content.PadColor);
                else
                {
                    var anchor = AnsiConsole.Prompt(new SelectionPrompt<Anchor>().Title("Where should the window sit?")
                        .UseConverter(a => a switch { Anchor.Center => "Centered", Anchor.TopLeft => "Top-left corner", _ => "Custom X, Y" })
                        .AddChoices(Anchor.Center, Anchor.TopLeft, Anchor.Custom));
                    var (x, y) = anchor == Anchor.Custom
                        ? (AnsiConsole.Prompt(new TextPrompt<int>("X offset:").DefaultValue(0)), AnsiConsole.Prompt(new TextPrompt<int>("Y offset:").DefaultValue(0)))
                        : (0, 0);
                    plan = CropPlanner.Exact(info, w, h, anchor, x, y);
                }
                break;
            }
            catch (ArgumentException ex)
            {
                AnsiConsole.MarkupLine($"[red]✘ {Markup.Escape(ex.Message)}[/] Let's try again.\n");
            }
        }

        var output = OutputNamer.Next(source, File.Exists);
        ShowPlan(plan, info, output);
        if (!AnsiConsole.Confirm("Start cropping?")) return;

        await AnsiConsole.Progress()
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new ElapsedTimeColumn(), new RemainingTimeColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("Cropping", maxValue: 1);
                await svc.CropAsync(source, output, plan, info, new Progress<double>(v => task.Value = v), ct);
            });

        AnsiConsole.Write(new Panel(new Rows(
                new Markup($"[green bold]✔ Done[/]"),
                new Markup($"[grey]File:[/]  [blue]{Markup.Escape(output)}[/]"),
                new Markup($"[grey]Size:[/]  {plan.OutWidth}×{plan.OutHeight}  ·  {Size(new FileInfo(output).Length)}  ·  audio {(info.AudioCodec is null ? "none" : "kept")}")))
            .Border(BoxBorder.Rounded).BorderColor(Color.Green).Padding(1, 0));

        if (AnsiConsole.Confirm("Show it in Explorer?", defaultValue: false))
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{output}\"");
    }

    private static int AskEven(string prompt, int def) =>
        AnsiConsole.Prompt(new TextPrompt<int>(prompt).DefaultValue(def)
            .Validate(v => v > 0 && v % 2 == 0 ? ValidationResult.Success() : ValidationResult.Error("[red]Use a positive, even number.[/]")));

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
