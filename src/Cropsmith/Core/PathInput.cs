namespace Cropsmith.Core;

public static class PathInput
{
    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".wmv", ".flv" };

    /// <summary>Drag-and-drop into a terminal wraps paths in quotes; strip them.</summary>
    public static string Clean(string raw) => raw.Trim().Trim('"', '\'').Trim();

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));
}
