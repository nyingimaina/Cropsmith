using Cropsmith.Core;
using Xunit;

namespace Cropsmith.Tests;

public class AspectRatioTests
{
    [Theory]
    [InlineData(720, 720, 1280)]
    [InlineData(1080, 1080, 1920)]
    public void Dimensions_9x16(int width, int w, int h) =>
        Assert.Equal((w, h), new AspectRatio(9, 16).Dimensions(width));

    [Fact]
    public void Dimensions_4x5() =>
        Assert.Equal((1080, 1350), new AspectRatio(4, 5).Dimensions(1080));

    [Fact]
    public void Dimensions_Square() =>
        Assert.Equal((500, 500), new AspectRatio(1, 1).Dimensions(500));

    [Fact]
    public void Dimensions_RoundsHeightToTheNearestEvenNumber()
    {
        var (w, h) = new AspectRatio(9, 16).Dimensions(768); // 1365.33 -> 1366
        Assert.Equal(768, w);
        Assert.Equal(1366, h);
        Assert.Equal(0, h % 2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(721)]
    public void Dimensions_RejectsBadWidths(int width) =>
        Assert.Throws<ArgumentException>(() => new AspectRatio(9, 16).Dimensions(width));

    [Theory]
    [InlineData("4:5", 4, 5)]
    [InlineData(" 16 : 9 ", 16, 9)]
    [InlineData("21:9", 21, 9)]
    public void TryParse_Accepts(string text, int w, int h)
    {
        Assert.True(AspectRatio.TryParse(text, out var r));
        Assert.Equal(new AspectRatio(w, h), r);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("4")]
    [InlineData("0:5")]
    [InlineData("4:-5")]
    [InlineData("4:5:6")]
    public void TryParse_Rejects(string? text) => Assert.False(AspectRatio.TryParse(text, out _));

    [Fact]
    public void Label_IsWidthColonHeight() => Assert.Equal("9:16", new AspectRatio(9, 16).Label);

    [Fact]
    public void Presets_CoverTheCommonShapes()
    {
        var labels = AspectRatios.Presets.Select(r => r.Label).ToList();
        Assert.Equal(new[] { "9:16", "4:5", "1:1", "3:4", "2:3", "16:9", "4:3", "21:9" }, labels);
    }
}

public class FillPlannerTests
{
    private static readonly CropBox Frame = new(0, 0, 1920, 1080);

    [Fact]
    public void Fill_TallTargetFromWideContent_CropsTheSidesAndNeedsNoPadding()
    {
        var plan = CropPlanner.Fill(Frame, 1080, 1920, Anchor.Center, "black");
        Assert.Equal(1080, plan.Crop.Height);
        Assert.Equal(0, plan.Crop.Width % 2);
        Assert.Equal(657, plan.Crop.X);
        Assert.Equal(0, plan.Crop.Y);
        Assert.Equal((1080, 1920), (plan.OutWidth, plan.OutHeight));
        Assert.False(plan.NeedsPad);
        Assert.InRange((double)plan.Crop.Width / plan.Crop.Height, 0.5625 * 0.99, 0.5625 * 1.01);
    }

    [Fact]
    public void Fill_TopLeft_AnchorsToTheContentOrigin()
    {
        var plan = CropPlanner.Fill(new CropBox(40, 13, 578, 1034), 500, 500, Anchor.TopLeft, "white");
        Assert.Equal(new CropBox(40, 13, 578, 578), plan.Crop);
    }

    [Fact]
    public void Fill_Center_WorksInsideAnOffsetContentBox()
    {
        var plan = CropPlanner.Fill(new CropBox(40, 13, 578, 1034), 500, 500, Anchor.Center, "white");
        Assert.Equal(new CropBox(40, 13 + (1034 - 578) / 2, 578, 578), plan.Crop);
        Assert.Equal("white", plan.PadColor);
    }

    [Fact]
    public void Fill_WideTargetFromTallContent_CropsTopAndBottom()
    {
        var plan = CropPlanner.Fill(new CropBox(40, 13, 578, 1034), 1280, 720, Anchor.Center, "white");
        Assert.Equal(578, plan.Crop.Width);
        Assert.Equal(324, plan.Crop.Height);
    }

    [Fact]
    public void Fill_Custom_OffsetIsRelativeToTheContentBox()
    {
        var plan = CropPlanner.Fill(Frame, 1080, 1920, Anchor.Custom, "black", 100, 0);
        Assert.Equal(100, plan.Crop.X);
    }

    [Fact]
    public void Fill_Custom_RejectsAWindowThatLeavesTheContent() =>
        Assert.Throws<ArgumentException>(() => CropPlanner.Fill(Frame, 1080, 1920, Anchor.Custom, "black", 1500, 0));

    [Fact]
    public void Fill_SameAspectAsContent_KeepsTheWholeContent()
    {
        var plan = CropPlanner.Fill(Frame, 1280, 720, Anchor.Center, "black");
        Assert.Equal(Frame, plan.Crop);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(101, 100)]
    public void Fill_RejectsBadOutputSizes(int w, int h) =>
        Assert.Throws<ArgumentException>(() => CropPlanner.Fill(Frame, w, h, Anchor.Center, "black"));
}

public class ScaleFactorTests
{
    [Fact]
    public void ScaleFactor_AboveOne_WhenUpscaling() =>
        Assert.Equal(2.0, new CropPlan(new CropBox(0, 0, 400, 300), 800, 600, 800, 600, "black").ScaleFactor);

    [Fact]
    public void ScaleFactor_BelowOne_WhenShrinking() =>
        Assert.Equal(0.5, new CropPlan(new CropBox(0, 0, 400, 300), 200, 150, 200, 150, "black").ScaleFactor);
}

public class SubtitleStateProbeTests
{
    private static string Json(string disposition) =>
        """{"streams":[{"codec_type":"video","width":1920,"height":1080},{"codec_type":"subtitle","codec_name":"mov_text" """
        + disposition
        + """}],"format":{"duration":"10"}}""";

    [Fact]
    public void ParseFull_ReadsSubtitlesOnByDefault()
    {
        var v = VideoProbe.ParseFull(Json(""","disposition":{"default":1}"""));
        Assert.True(v.HasSubtitles);
        Assert.True(v.SubtitlesOnByDefault);
    }

    [Fact]
    public void ParseFull_ReadsSubtitlesOffByDefault()
    {
        var v = VideoProbe.ParseFull(Json(""","disposition":{"default":0}"""));
        Assert.True(v.HasSubtitles);
        Assert.False(v.SubtitlesOnByDefault);
    }

    [Fact]
    public void ParseFull_NoSubtitles_ReportsOff()
    {
        var v = VideoProbe.ParseFull("""{"streams":[{"codec_type":"video","width":10,"height":10}],"format":{}}""");
        Assert.False(v.HasSubtitles);
        Assert.False(v.SubtitlesOnByDefault);
    }

    [Fact]
    public void ParseFull_MatchesParse_ForEverythingElse()
    {
        var json = Json(""","disposition":{"default":1}""");
        var basic = VideoProbe.Parse(json);
        var full = VideoProbe.ParseFull(json);
        Assert.Equal((basic.Width, basic.Height, basic.Duration, basic.AudioCodec, basic.HasSubtitles),
                     (full.Width, full.Height, full.Duration, full.AudioCodec, full.HasSubtitles));
    }
}
