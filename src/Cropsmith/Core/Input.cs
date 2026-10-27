namespace Cropsmith.Core;

public enum KeyAction { None, Submit, Back, Quit }

public static class KeyMap
{
    public static bool IsCtrl(ConsoleKeyInfo k, ConsoleKey key) =>
        (k.Modifiers & ConsoleModifiers.Control) != 0 && k.Key == key;

    public static bool IsQuit(ConsoleKeyInfo k) => IsCtrl(k, ConsoleKey.C) || IsCtrl(k, ConsoleKey.Q);

    /// <summary>Keys that abort a running job (crop or download).</summary>
    public static bool IsCancel(ConsoleKeyInfo k) => k.Key == ConsoleKey.Escape || IsQuit(k);
}

/// <summary>Single-line editor state: Esc goes back, Ctrl+C / Ctrl+Q quit, everything else edits.</summary>
public sealed class LineEditor(string initial = "")
{
    public string Text { get; private set; } = initial;
    public int Cursor { get; private set; } = initial.Length;

    public KeyAction Apply(ConsoleKeyInfo k)
    {
        if (KeyMap.IsQuit(k)) return KeyAction.Quit;
        switch (k.Key)
        {
            case ConsoleKey.Enter: return KeyAction.Submit;
            case ConsoleKey.Escape: return KeyAction.Back;
            case ConsoleKey.LeftArrow: Cursor = Math.Max(0, Cursor - 1); return KeyAction.None;
            case ConsoleKey.RightArrow: Cursor = Math.Min(Text.Length, Cursor + 1); return KeyAction.None;
            case ConsoleKey.Home: Cursor = 0; return KeyAction.None;
            case ConsoleKey.End: Cursor = Text.Length; return KeyAction.None;
            case ConsoleKey.Backspace:
                if (Cursor > 0) { Text = Text.Remove(Cursor - 1, 1); Cursor--; }
                return KeyAction.None;
            case ConsoleKey.Delete:
                if (Cursor < Text.Length) Text = Text.Remove(Cursor, 1);
                return KeyAction.None;
        }
        if (KeyMap.IsCtrl(k, ConsoleKey.U)) { Text = ""; Cursor = 0; }
        else if (!char.IsControl(k.KeyChar)) { Text = Text.Insert(Cursor, k.KeyChar.ToString()); Cursor++; }
        return KeyAction.None;
    }

    /// <summary>The slice of text that fits in <paramref name="width"/> columns and where the cursor sits inside it.</summary>
    public (string Visible, int CursorColumn) View(int width)
    {
        var avail = Math.Max(1, width - 1);
        if (Text.Length <= avail) return (Text, Cursor);
        var start = Math.Max(0, Cursor - avail);
        return (Text.Substring(start, Math.Min(avail, Text.Length - start)), Cursor - start);
    }
}

/// <summary>Selection-list state: arrows/Home/End/PageUp/PageDown move, Enter picks, Esc goes back, Q quits.</summary>
public sealed class ListNavigator
{
    private readonly int _count;
    private readonly int _page;

    public ListNavigator(int count, int pageSize, int initial = 0)
    {
        _count = count;
        _page = Math.Max(1, pageSize);
        Index = Math.Clamp(initial, 0, Math.Max(0, count - 1));
        Scroll();
    }

    public int Index { get; private set; }
    public int Start { get; private set; }
    public int Visible => Math.Min(_page, _count);

    public KeyAction Apply(ConsoleKeyInfo k)
    {
        if (KeyMap.IsQuit(k) || char.ToLowerInvariant(k.KeyChar) == 'q') return KeyAction.Quit;
        switch (k.Key)
        {
            case ConsoleKey.Enter: return KeyAction.Submit;
            case ConsoleKey.Escape: return KeyAction.Back;
            case ConsoleKey.UpArrow: Index = (Index - 1 + _count) % _count; break;
            case ConsoleKey.DownArrow: Index = (Index + 1) % _count; break;
            case ConsoleKey.Home: Index = 0; break;
            case ConsoleKey.End: Index = _count - 1; break;
            case ConsoleKey.PageUp: Index = Math.Max(0, Index - _page); break;
            case ConsoleKey.PageDown: Index = Math.Min(_count - 1, Index + _page); break;
        }
        Scroll();
        return KeyAction.None;
    }

    private void Scroll()
    {
        if (Index < Start) Start = Index;
        else if (Index >= Start + _page) Start = Index - _page + 1;
    }
}

