using Cropsmith.Core;
using Xunit;

namespace Cropsmith.Tests;

internal static class K
{
    public static ConsoleKeyInfo Key(ConsoleKey key, char ch = '\0', bool ctrl = false) => new(ch, key, false, false, ctrl);
    public static ConsoleKeyInfo Ch(char c) => new(c, ConsoleKey.A, false, false, false);
    public static readonly ConsoleKeyInfo Enter = Key(ConsoleKey.Enter, '\r');
    public static readonly ConsoleKeyInfo Esc = Key(ConsoleKey.Escape, '\x1b');
    public static readonly ConsoleKeyInfo CtrlC = Key(ConsoleKey.C, '\x03', ctrl: true);
    public static readonly ConsoleKeyInfo CtrlQ = Key(ConsoleKey.Q, '\x11', ctrl: true);
}

public class LineEditorTests
{
    private static LineEditor Typed(string initial, params ConsoleKeyInfo[] keys)
    {
        var e = new LineEditor(initial);
        foreach (var k in keys) e.Apply(k);
        return e;
    }

    [Fact]
    public void StartsWithInitialText_CursorAtEnd()
    {
        var e = new LineEditor("768");
        Assert.Equal("768", e.Text);
        Assert.Equal(3, e.Cursor);
    }

    [Fact]
    public void TypingAppends() =>
        Assert.Equal("12", Typed("", K.Ch('1'), K.Ch('2')).Text);

    [Fact]
    public void Backspace_RemovesBeforeCursor() =>
        Assert.Equal("76", Typed("768", K.Key(ConsoleKey.Backspace, '\b')).Text);

    [Fact]
    public void Backspace_AtStart_DoesNothing() =>
        Assert.Equal("7", Typed("7", K.Key(ConsoleKey.Home), K.Key(ConsoleKey.Backspace, '\b')).Text);

    [Fact]
    public void Delete_RemovesAtCursor() =>
        Assert.Equal("68", Typed("768", K.Key(ConsoleKey.Home), K.Key(ConsoleKey.Delete)).Text);

    [Fact]
    public void ArrowsMoveCursor_AndInsertGoesInTheMiddle()
    {
        var e = Typed("78", K.Key(ConsoleKey.LeftArrow), K.Ch('6'));
        Assert.Equal("768", e.Text);
        Assert.Equal(2, e.Cursor);
    }

    [Fact]
    public void RightArrow_StopsAtEnd() =>
        Assert.Equal(2, Typed("78", K.Key(ConsoleKey.RightArrow), K.Key(ConsoleKey.RightArrow)).Cursor);

    [Fact]
    public void EndKey_JumpsToEnd() =>
        Assert.Equal(3, Typed("768", K.Key(ConsoleKey.Home), K.Key(ConsoleKey.End)).Cursor);

    [Fact]
    public void CtrlU_ClearsLine()
    {
        var e = Typed("768", K.Key(ConsoleKey.U, '\x15', ctrl: true));
        Assert.Equal("", e.Text);
        Assert.Equal(0, e.Cursor);
    }

    [Fact]
    public void Enter_Submits() => Assert.Equal(KeyAction.Submit, new LineEditor("1").Apply(K.Enter));

    [Fact]
    public void Escape_GoesBack_EvenWithText() => Assert.Equal(KeyAction.Back, new LineEditor("123").Apply(K.Esc));

    [Fact]
    public void CtrlC_AndCtrlQ_Quit()
    {
        Assert.Equal(KeyAction.Quit, new LineEditor().Apply(K.CtrlC));
        Assert.Equal(KeyAction.Quit, new LineEditor().Apply(K.CtrlQ));
    }

    [Fact]
    public void PlainQ_IsJustACharacter()
    {
        var e = new LineEditor();
        Assert.Equal(KeyAction.None, e.Apply(K.Ch('q')));
        Assert.Equal("q", e.Text);
    }

    [Fact]
    public void View_ShowsEverything_WhenItFits()
    {
        var (visible, col) = new LineEditor("hello").View(20);
        Assert.Equal("hello", visible);
        Assert.Equal(5, col);
    }

    [Fact]
    public void View_ScrollsToKeepCursorVisible()
    {
        var (visible, col) = new LineEditor("01234567890123456789").View(11);
        Assert.Equal("0123456789", visible);
        Assert.Equal(10, col);
    }

    [Fact]
    public void View_ShowsStart_WhenCursorAtHome()
    {
        var e = new LineEditor("01234567890123456789");
        e.Apply(K.Key(ConsoleKey.Home));
        var (visible, col) = e.View(11);
        Assert.Equal("0123456789", visible);
        Assert.Equal(0, col);
    }
}

public class ListNavigatorTests
{
    [Fact]
    public void Down_MovesAndWraps()
    {
        var n = new ListNavigator(3, 10);
        n.Apply(K.Key(ConsoleKey.DownArrow));
        Assert.Equal(1, n.Index);
        n.Apply(K.Key(ConsoleKey.DownArrow));
        n.Apply(K.Key(ConsoleKey.DownArrow));
        Assert.Equal(0, n.Index);
    }

    [Fact]
    public void Up_WrapsToLast()
    {
        var n = new ListNavigator(3, 10);
        n.Apply(K.Key(ConsoleKey.UpArrow));
        Assert.Equal(2, n.Index);
    }

    [Fact]
    public void HomeEnd_Jump()
    {
        var n = new ListNavigator(50, 10);
        n.Apply(K.Key(ConsoleKey.End));
        Assert.Equal(49, n.Index);
        n.Apply(K.Key(ConsoleKey.Home));
        Assert.Equal(0, n.Index);
    }

    [Fact]
    public void PageKeys_MoveByPage_AndClamp()
    {
        var n = new ListNavigator(25, 10);
        n.Apply(K.Key(ConsoleKey.PageDown));
        Assert.Equal(10, n.Index);
        n.Apply(K.Key(ConsoleKey.PageDown));
        n.Apply(K.Key(ConsoleKey.PageDown));
        Assert.Equal(24, n.Index);
        n.Apply(K.Key(ConsoleKey.PageUp));
        Assert.Equal(14, n.Index);
    }

    [Fact]
    public void Window_FollowsSelection()
    {
        var n = new ListNavigator(30, 5);
        for (var i = 0; i < 7; i++) n.Apply(K.Key(ConsoleKey.DownArrow));
        Assert.Equal(3, n.Start);
        Assert.Equal(5, n.Visible);
        n.Apply(K.Key(ConsoleKey.End));
        Assert.Equal(25, n.Start);
        n.Apply(K.Key(ConsoleKey.Home));
        Assert.Equal(0, n.Start);
    }

    [Fact]
    public void Visible_IsCappedByCount() => Assert.Equal(2, new ListNavigator(2, 10).Visible);

    [Fact]
    public void RespectsInitialIndex() => Assert.Equal(1, new ListNavigator(2, 10, initial: 1).Index);

    [Fact]
    public void EnterSubmits_EscBacks_QAndCtrlCQuit()
    {
        var n = new ListNavigator(3, 10);
        Assert.Equal(KeyAction.Submit, n.Apply(K.Enter));
        Assert.Equal(KeyAction.Back, n.Apply(K.Esc));
        Assert.Equal(KeyAction.Quit, n.Apply(K.Ch('q')));
        Assert.Equal(KeyAction.Quit, n.Apply(K.CtrlC));
    }
}

public class CancelKeyTests
{
    [Fact]
    public void EscAndCtrlC_CancelARunningJob()
    {
        Assert.True(KeyMap.IsCancel(K.Esc));
        Assert.True(KeyMap.IsCancel(K.CtrlC));
        Assert.False(KeyMap.IsCancel(K.Ch('x')));
    }
}
