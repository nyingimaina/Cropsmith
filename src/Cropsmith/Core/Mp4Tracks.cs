using System.Buffers.Binary;
using System.Text;

namespace Cropsmith.Core;

/// <summary>
/// ffmpeg's MP4 muxer always marks the first subtitle track as enabled, whatever -disposition says.
/// Players read that "enabled" bit to decide what to show, so clearing it in place turns subtitles
/// off by default while keeping the track selectable.
/// </summary>
public static class Mp4Tracks
{
    private const byte EnabledBit = 0x01;
    private static readonly HashSet<string> SubtitleHandlers = ["sbtl", "subt", "text", "clcp"];

    /// <summary>Clears the enabled flag on every subtitle track. Returns how many tracks were changed.</summary>
    public static int DisableSubtitleTracks(Stream mp4)
    {
        var changed = 0;
        try { Walk(mp4, 0, mp4.Length, ref changed); }
        catch (EndOfStreamException) { }
        return changed;
    }

    private static void Walk(Stream s, long start, long end, ref int changed)
    {
        foreach (var box in Boxes(s, start, end))
        {
            if (box.Type is "moov") Walk(s, box.Body, box.End, ref changed);
            else if (box.Type is "trak" && DisableIfSubtitle(s, box)) changed++;
        }
    }

    private static bool DisableIfSubtitle(Stream s, Box trak)
    {
        long? tkhdFlags = null;
        string? handler = null;

        foreach (var child in Boxes(s, trak.Body, trak.End))
        {
            if (child.Type == "tkhd") tkhdFlags = child.Body + 3; // version(1) + flags(3): last flag byte
            else if (child.Type == "mdia")
                foreach (var m in Boxes(s, child.Body, child.End))
                    if (m.Type == "hdlr") handler = ReadAscii(s, m.Body + 8, 4); // fullbox(4) + pre_defined(4)
        }

        if (tkhdFlags is null || handler is null || !SubtitleHandlers.Contains(handler)) return false;

        s.Position = tkhdFlags.Value;
        var flags = (byte)s.ReadByte();
        if ((flags & EnabledBit) == 0) return false;
        s.Position = tkhdFlags.Value;
        s.WriteByte((byte)(flags & ~EnabledBit));
        return true;
    }

    private readonly record struct Box(string Type, long Body, long End);

    private static IEnumerable<Box> Boxes(Stream s, long start, long end)
    {
        var pos = start;
        while (pos + 8 <= end)
        {
            s.Position = pos;
            Span<byte> head = stackalloc byte[8];
            s.ReadExactly(head);
            long size = BinaryPrimitives.ReadUInt32BigEndian(head);
            var type = Encoding.ASCII.GetString(head[4..]);
            var headerLen = 8;

            if (size == 1)
            {
                Span<byte> big = stackalloc byte[8];
                s.ReadExactly(big);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(big);
                headerLen = 16;
            }
            else if (size == 0) size = end - pos;

            if (size < headerLen || pos + size > end) yield break; // corrupt or truncated: stop quietly
            yield return new Box(type, pos + headerLen, pos + size);
            pos += size;
        }
    }

    private static string ReadAscii(Stream s, long at, int len)
    {
        s.Position = at;
        var buf = new byte[len];
        s.ReadExactly(buf);
        return Encoding.ASCII.GetString(buf);
    }
}
