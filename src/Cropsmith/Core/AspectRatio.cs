namespace Cropsmith.Core;

public readonly record struct AspectRatio(int Width, int Height)
{
    public string Label => $"{Width}:{Height}";

    /// <summary>Output size for a chosen width; the height is rounded to the nearest even number.</summary>
    public (int Width, int Height) Dimensions(int outputWidth)
    {
        if (outputWidth <= 0 || outputWidth % 2 != 0)
            throw new ArgumentException("Width must be a positive, even number.");
        var height = (int)Math.Round(outputWidth * (double)Height / Width / 2.0) * 2;
        return (outputWidth, Math.Max(2, height));
    }

    public static bool TryParse(string? text, out AspectRatio ratio)
    {
        ratio = default;
        var parts = text?.Split(':');
        if (parts is not { Length: 2 }) return false;
        if (!int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h) || w <= 0 || h <= 0) return false;
        ratio = new AspectRatio(w, h);
        return true;
    }
}

public static class AspectRatios
{
    public static IReadOnlyList<AspectRatio> Presets { get; } =
    [
        new(9, 16), new(4, 5), new(1, 1), new(3, 4), new(2, 3), new(16, 9), new(4, 3), new(21, 9),
    ];
}
