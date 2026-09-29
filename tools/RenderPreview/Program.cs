using System.Collections.Generic;
using System.IO;
using ClaudeUsageWidget;
using SkiaSharp;

// Usage: dotnet run --project tools/RenderPreview -- <output dir>
// Writes widget.png, compact.png and settings.png (2x, transparent background).
// Values are samples, not real usage, so the images are safe to commit.
var outDir = args.Length > 0 ? args[0] : "docs";
Directory.CreateDirectory(outDir);

var full = new List<UsageRow>
{
    new("현재 세션", 28, "리셋 18:50", RowIcon.Clock),
    new("주간", 66, "리셋 10/2(목)", RowIcon.Calendar),
};
var compact = new List<UsageRow>
{
    new("세션", 28, "", RowIcon.Clock),
    new("주간", 66, "", RowIcon.Calendar),
};

Render(new WidgetState { Page = WidgetPage.Usage, Rows = full }, "widget.png");
Render(new WidgetState { Page = WidgetPage.Usage, Compact = true, Rows = compact }, "compact.png");
Render(new WidgetState { Page = WidgetPage.Settings, StartupEnabled = true, PollMinutes = 5 }, "settings.png");

void Render(WidgetState state, string name)
{
    var hits = new List<HitRegion>();
    float height = WidgetRenderer.Draw(null, state, hits);
    const float scale = 2;

    using var surface = SKSurface.Create(new SKImageInfo(
        (int)(WidgetRenderer.WidthFor(state) * scale), (int)(height * scale)));
    surface.Canvas.Clear(SKColors.Transparent);
    surface.Canvas.Scale(scale);
    WidgetRenderer.Draw(surface.Canvas, state, hits);

    using var png = surface.Snapshot().Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(Path.Combine(outDir, name), png.ToArray());
}
