using System.Globalization;
using System.Reflection;

namespace Cropsmith.Core;

/// <summary>When this build was compiled; the csproj bakes the UTC time into assembly metadata.</summary>
public static class BuildStamp
{
    private const string Unknown = "build time unknown";

    public static string Current() =>
        Format(typeof(BuildStamp).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildTimeUtc")?.Value);

    public static string Format(string? utcIso) =>
        DateTime.TryParse(utcIso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)
            ? $"built {utc.ToLocalTime():yyyy-MM-dd HH:mm}"
            : Unknown;
}
