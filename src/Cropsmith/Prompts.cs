using Cropsmith.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cropsmith;

/// <summary>Esc pressed: go to the previous step.</summary>
internal sealed class BackException : Exception;

/// <summary>Ctrl+C / Ctrl+Q / Q pressed: leave the app.</summary>
internal sealed class QuitException : Exception;

internal static class Prompts
{
    private const string ListHint = "[grey]↑↓ move · Enter select · Esc back · Q quit[/]";

    public static T Select<T>(string title, IReadOnlyList<T> items, Func<T, string> label, int initial = 0, int pageSize = 12)
    {
        var nav = new ListNavigator(items.Count, pageSize, initial);
        KeyAction action = KeyAction.None;

        AnsiConsole.Live(new Markup("")).AutoClear(true).Start(ctx =>
        {
            while (true)
            {
                var rows = new List<IRenderable> { new Markup($"[bold]{title}[/]") };
                if (nav.Start > 0) rows.Add(new Markup("[grey]  ↑ more[/]"));
                for (var i = nav.Start; i < nav.Start + nav.Visible; i++)
                    rows.Add(new Markup(i == nav.Index ? $"[orange1]❯[/] [bold]{label(items[i])}[/]" : $"  {label(items[i])}"));
                if (nav.Start + nav.Visible < items.Count) rows.Add(new Markup("[grey]  ↓ more[/]"));
                rows.Add(new Markup(ListHint));
                ctx.UpdateTarget(new Rows(rows));
                ctx.Refresh();

                action = nav.Apply(Console.ReadKey(true));
                if (action != KeyAction.None) return;
            }
        });

        switch (action)
        {
            case KeyAction.Back: throw new BackException();
            case KeyAction.Quit: throw new QuitException();
        }
        AnsiConsole.MarkupLine($"{title} [orange1]›[/] {label(items[nav.Index])}");
        return items[nav.Index];
    }

    /// <summary>Editable single-line input. <paramref name="parse"/> returns the value, or an error message to show and retry.</summary>
    public static T Ask<T>(string prompt, string initial, Func<string, (T? Value, string? Error)> parse)
    {
        while (true)
        {
            AnsiConsole.Markup($"{prompt} ");
            var (left, top) = (Console.CursorLeft, Console.CursorTop);
            var width = Math.Max(8, Console.WindowWidth - left - 1);
            var editor = new LineEditor(initial);
            var action = KeyAction.None;

            void Render()
            {
                var (visible, col) = editor.View(width);
                Console.SetCursorPosition(left, top);
                Console.Write(visible.PadRight(width));
                Console.SetCursorPosition(left + col, top);
            }

            Render();
            while (action == KeyAction.None)
            {
                action = editor.Apply(Console.ReadKey(true));
                if (action == KeyAction.None) Render();
            }
            Console.SetCursorPosition(left, top);
            Console.Write(editor.View(width).Visible.PadRight(width));
            Console.SetCursorPosition(left, top);
            AnsiConsole.WriteLine(editor.View(width).Visible);

            switch (action)
            {
                case KeyAction.Back: throw new BackException();
                case KeyAction.Quit: throw new QuitException();
            }

            var (value, error) = parse(editor.Text);
            if (error is null) return value!;
            AnsiConsole.MarkupLine($"[red]  {Markup.Escape(error)}[/]");
            initial = editor.Text;
        }
    }

    public static bool Confirm(string question, bool defaultYes = true) =>
        Select(question, new[] { true, false }, v => v ? "Yes" : "No", defaultYes ? 0 : 1);

    public static Task Watch(CancellationTokenSource job, CancellationToken stop) => Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            if (Console.KeyAvailable && KeyMap.IsCancel(Console.ReadKey(true))) { job.Cancel(); return; }
            await Task.Delay(40);
        }
    });
}

