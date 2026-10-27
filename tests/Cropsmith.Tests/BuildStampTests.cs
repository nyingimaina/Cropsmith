using Cropsmith.Core;
using Xunit;

namespace Cropsmith.Tests;

public class BuildStampTests
{
    [Fact]
    public void Format_ShowsUtcTimestampInLocalTime()
    {
        var utc = new DateTime(2026, 10, 27, 13, 18, 5, DateTimeKind.Utc);
        var expected = utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        Assert.Equal($"built {expected}", BuildStamp.Format(utc.ToString("o")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    public void Format_FallsBack_WhenMissingOrUnreadable(string? raw) =>
        Assert.Equal("build time unknown", BuildStamp.Format(raw));

    [Fact]
    public void Current_ReadsTheStampBakedIntoThisAssembly() =>
        Assert.NotEqual("build time unknown", BuildStamp.Current());
}
