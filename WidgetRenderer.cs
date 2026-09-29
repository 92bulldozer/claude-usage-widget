using System;
using System.Collections.Generic;
using SkiaSharp;

namespace ClaudeUsageWidget;

public enum RowIcon { Clock, Calendar }

public sealed record UsageRow(string Label, double Used, string ResetText, RowIcon Icon);

public enum WidgetPage { Usage, Settings }

public static class HitId
{
    public const string Settings = "settings";
    public const string Close = "close";
    public const string ToggleCompact = "compact";
    public const string Back = "back";
    public const string Login = "login";
    public const string Startup = "toggle:startup";
    public const string Topmost = "toggle:topmost";
    public const string Lock = "toggle:lock";
    public const string CompactSetting = "toggle:compact";
    public const string PollPrefix = "poll:";
    public const string Refresh = "action:refresh";
    public const string Relogin = "action:login";
}

/// <summary>A clickable area produced by the renderer, in DIPs.</summary>
public readonly record struct HitRegion(string Id, SKRect Rect);

public sealed class WidgetState
{
    public WidgetPage Page { get; init; }
    public bool Compact { get; init; }
    public string? Hover { get; init; }

    public List<UsageRow> Rows { get; init; } = new();
    public string? Error { get; init; }
    public bool ErrorIsLink { get; init; }

    public bool StartupEnabled { get; init; }
    public bool Topmost { get; init; }
    public bool Locked { get; init; }
    public int PollMinutes { get; init; }
}

/// <summary>
/// Draws the whole widget with Skia. Coordinates are in WPF device-independent
/// pixels; the caller scales the canvas for DPI. Passing a null canvas only
/// measures, so the window can be sized before painting.
/// </summary>
public static class WidgetRenderer
{
    /// <summary>Transparent room around the card for its drop shadow.</summary>
    public const float Margin = 10;
    public const float CardWidth = 260;
    public const float Width = CardWidth + Margin * 2;
    public const float CompactCardWidth = 210;

    public static float WidthFor(WidgetState state)
        => (state.Compact && state.Page == WidgetPage.Usage ? CompactCardWidth : CardWidth) + Margin * 2;

    private const float CardRadius = 12;
    private const float PadX = 12;
    private const float HeaderHeight = 32;
    private const float Inset = 6;
    private const float RowHeight = 46;
    private const float RowGap = 6;
    private const float RowRadius = 9;
    private const float IconSize = 26;
    private const float BarHeight = 6;

    private readonly record struct Tone(SKColor Base, SKColor Light, SKColor Dark);

    private static readonly Tone GreenTone = new(new(0x7A, 0xCB, 0x91), new(0x92, 0xDB, 0xA6), new(0x5C, 0xB0, 0x78));
    private static readonly Tone AmberTone = new(new(0xE6, 0xC2, 0x88), new(0xF2, 0xD4, 0xA0), new(0xD2, 0xA5, 0x62));
    private static readonly Tone RedTone = new(new(0xE8, 0x7C, 0x70), new(0xF2, 0x94, 0x88), new(0xCC, 0x5E, 0x52));

    private static readonly SKColor TitleOrangeLight = new(0xF0, 0x8A, 0x68);
    private static readonly SKColor TitleOrange = new(0xDB, 0x6E, 0x4E);
    private static readonly SKColor TitleGray = new(0xB4, 0xB8, 0xBE);
    private static readonly SKColor TextPrimary = new(0xF2, 0xF3, 0xF5);
    private static readonly SKColor TextMuted = new(0x9A, 0xA0, 0xA6);
    private static readonly SKColor TextDim = new(0x7E, 0x84, 0x8A);
    private static readonly SKColor Hairline = new(0xFF, 0xFF, 0xFF, 0x14);
    private static readonly SKColor ErrorColor = new(0xF0, 0xA8, 0x98);

    // Pretendard is bundled as an embedded resource so the widget looks the same
    // on PCs without it installed; installed fonts are only fallbacks.
    private static readonly SKTypeface Regular = LoadTypeface("Regular", SKFontStyleWeight.Normal);
    private static readonly SKTypeface Bold = LoadTypeface("Bold", SKFontStyleWeight.Bold);

    private static SKTypeface LoadTypeface(string pretendardWeight, SKFontStyleWeight weight)
    {
        using var stream = typeof(WidgetRenderer).Assembly
            .GetManifestResourceStream($"ClaudeUsageWidget.Fonts.Pretendard-{pretendardWeight}.ttf");
        // Copy into SKData: the typeface reads glyphs lazily, after the stream is disposed.
        if (stream is not null && SKTypeface.FromData(SKData.Create(stream)) is { } embedded)
            return embedded;

        var style = new SKFontStyle(weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        return SKFontManager.Default.MatchFamily("Pretendard", style)
            ?? SKFontManager.Default.MatchFamily("Noto Sans KR", style)
            ?? SKFontManager.Default.MatchFamily("Malgun Gothic", style)
            ?? SKTypeface.Default;
    }

    private static Tone ToneFor(double used) => used < 50 ? GreenTone : used < 80 ? AmberTone : RedTone;

    public static SKColor ColorFor(double used) => ToneFor(used).Base;

    private static readonly Dictionary<(SKTypeface, float), SKFont> FontCache = new();

    /// <summary>Shared, never-disposed font instances; the renderer only uses a handful of sizes.</summary>
    private static SKFont Font(SKTypeface typeface, float size)
    {
        if (!FontCache.TryGetValue((typeface, size), out var font))
        {
            // Grayscale AA: subpixel (LCD) AA fringes badly on a transparent surface.
            font = new SKFont(typeface, size) { Subpixel = true, Edging = SKFontEdging.Antialias };
            FontCache[(typeface, size)] = font;
        }
        return font;
    }

    // The usage API rate-limits aggressively, so nothing under 3 minutes.
    public static readonly int[] PollChoices = { 3, 5, 10, 30 };

    private static readonly SKColor Accent = new(0xDB, 0x6E, 0x4E);
    private static readonly SKColor Danger = new(0xE8, 0x7C, 0x70);
    private static readonly SKColor HoverFill = new(0xFF, 0xFF, 0xFF, 0x0D);

    /// <summary>
    /// Lays out and (if <paramref name="canvas"/> is set) draws the current page;
    /// fills <paramref name="hits"/> with clickable regions and returns total height.
    /// </summary>
    public static float Draw(SKCanvas? canvas, WidgetState state, List<HitRegion> hits)
    {
        hits.Clear();
        if (state.Page == WidgetPage.Settings) return DrawSettings(canvas, state, hits);
        return state.Compact ? DrawCompact(canvas, state, hits) : DrawUsage(canvas, state, hits);
    }

    // ---- Compact view: one slim line per limit, no titles or reset times ----

    private const float CompactRowHeight = 20;
    private const float CompactButton = 18;

    private static float DrawCompact(SKCanvas? canvas, WidgetState state, List<HitRegion> hits)
    {
        var labelFont = Font(Regular, 11);
        var valueFont = Font(Bold, 12.5f);
        var errorFont = Font(Regular, 10.5f);

        float cardX = Margin, cardY = Margin;
        float cardRight = cardX + CompactCardWidth;
        float buttonsLeft = cardRight - 6 - CompactButton;
        float contentRight = buttonsLeft - 6;

        var errorLines = state.Error is null ? new List<string>() : Wrap(state.Error, errorFont, contentRight - cardX - 10);
        float rowsHeight = state.Rows.Count * CompactRowHeight;
        float errorHeight = errorLines.Count * errorFont.Spacing;
        float rowsToError = errorLines.Count > 0 && rowsHeight > 0 ? 4 : 0;
        // Tall enough for the two stacked buttons even with a single row.
        float contentHeight = Math.Max(rowsHeight + rowsToError + errorHeight, CompactButton * 2 + 2 - 4);
        float cardBottom = cardY + 8 + contentHeight + 8;
        float totalHeight = cardBottom + Margin;

        var expand = SKRect.Create(buttonsLeft, cardY + 6, CompactButton, CompactButton);
        var close = SKRect.Create(buttonsLeft, expand.Bottom + 2, CompactButton, CompactButton);
        hits.Add(new HitRegion(HitId.ToggleCompact, expand));
        hits.Add(new HitRegion(HitId.Close, close));

        float errorTop = cardY + 8 + rowsHeight + rowsToError;
        if (errorLines.Count > 0 && state.ErrorIsLink)
            hits.Add(new HitRegion(HitId.Login, new SKRect(cardX + 10, errorTop, contentRight, errorTop + errorHeight)));

        if (canvas is null) return totalHeight;

        var card = new SKRect(cardX, cardY, cardRight, cardBottom);
        DrawCard(canvas, card);
        DrawIconButton(canvas, expand, state.Hover == HitId.ToggleCompact, DrawExpand, 5);
        DrawIconButton(canvas, close, state.Hover == HitId.Close, DrawClose, 5);

        using var paint = new SKPaint { IsAntialias = true };
        float y = cardY + 8;
        foreach (var row in state.Rows)
        {
            var tone = ToneFor(row.Used);
            float baseline = y + CompactRowHeight / 2 + 4;

            paint.Color = TextMuted;
            canvas.DrawText(row.Label, cardX + 10, baseline, SKTextAlign.Left, labelFont, paint);

            paint.Color = tone.Base;
            canvas.DrawText($"{Math.Round(row.Used)}%", contentRight, baseline, SKTextAlign.Right, valueFont, paint);

            var track = new SKRect(cardX + 10 + 42, y + CompactRowHeight / 2 - 2.5f, contentRight - 38, y + CompactRowHeight / 2 + 2.5f);
            paint.Color = new SKColor(0x29, 0x2D, 0x32);
            canvas.DrawRoundRect(track, 2.5f, 2.5f, paint);
            float fillW = (float)(track.Width * Math.Clamp(row.Used, 0, 100) / 100);
            if (fillW > 0)
            {
                var fill = new SKRect(track.Left, track.Top, track.Left + Math.Max(fillW, track.Height), track.Bottom);
                paint.Color = SKColors.White;
                paint.Shader = SKShader.CreateLinearGradient(
                    new SKPoint(fill.Left, 0), new SKPoint(fill.Right, 0),
                    new[] { tone.Dark, tone.Light }, SKShaderTileMode.Clamp);
                canvas.DrawRoundRect(fill, 2.5f, 2.5f, paint);
                paint.Shader = null;
            }

            y += CompactRowHeight;
        }

        if (errorLines.Count > 0)
        {
            paint.Color = ErrorColor;
            float ly = errorTop;
            foreach (var line in errorLines)
            {
                float baseline = ly - errorFont.Metrics.Ascent;
                canvas.DrawText(line, cardX + 10, baseline, SKTextAlign.Left, errorFont, paint);
                if (state.ErrorIsLink)
                    canvas.DrawRect(new SKRect(cardX + 10, baseline + 2, cardX + 10 + errorFont.MeasureText(line), baseline + 3), paint);
                ly += errorFont.Spacing;
            }
        }

        return totalHeight;
    }

    private static float DrawUsage(SKCanvas? canvas, WidgetState state, List<HitRegion> hits)
    {
        var errorFont = Font(Regular, 11);
        float cardX = Margin, cardY = Margin;
        float cardRight = cardX + CardWidth;

        var errorLines = state.Error is null ? new List<string>() : Wrap(state.Error, errorFont, CardWidth - PadX * 2);
        float errorLineHeight = errorFont.Spacing;
        int n = state.Rows.Count;

        float rowsTop = cardY + HeaderHeight;
        float rowsBottom = n > 0 ? rowsTop + n * RowHeight + (n - 1) * RowGap : rowsTop - 4;
        float errorTop = rowsBottom + (errorLines.Count > 0 ? 8 : 0);
        float contentBottom = errorTop + errorLines.Count * errorLineHeight;
        float cardBottom = contentBottom + (errorLines.Count > 0 ? 10 : Inset);
        float totalHeight = cardBottom + Margin;

        var close = SKRect.Create(cardRight - 6 - 22, cardY + 6, 22, 22);
        var gear = SKRect.Create(close.Left - 24, cardY + 6, 22, 22);
        var compact = SKRect.Create(gear.Left - 24, cardY + 6, 22, 22);
        hits.Add(new HitRegion(HitId.ToggleCompact, compact));
        hits.Add(new HitRegion(HitId.Settings, gear));
        hits.Add(new HitRegion(HitId.Close, close));
        if (errorLines.Count > 0 && state.ErrorIsLink)
            hits.Add(new HitRegion(HitId.Login, new SKRect(cardX + PadX, errorTop, cardRight - PadX, contentBottom)));

        if (canvas is null) return totalHeight;

        var card = new SKRect(cardX, cardY, cardRight, cardBottom);
        DrawCard(canvas, card);
        DrawTitle(canvas, card);
        DrawIconButton(canvas, compact, state.Hover == HitId.ToggleCompact, DrawCollapse);
        DrawIconButton(canvas, gear, state.Hover == HitId.Settings, DrawGear);
        DrawIconButton(canvas, close, state.Hover == HitId.Close, DrawClose);

        float y = rowsTop;
        foreach (var row in state.Rows)
        {
            DrawRow(canvas, new SKRect(cardX + Inset, y, cardRight - Inset, y + RowHeight), row);
            y += RowHeight + RowGap;
        }

        if (errorLines.Count > 0)
        {
            using var paint = new SKPaint { IsAntialias = true, Color = ErrorColor };
            float ly = errorTop;
            foreach (var line in errorLines)
            {
                float baseline = ly - errorFont.Metrics.Ascent;
                canvas.DrawText(line, cardX + PadX, baseline, SKTextAlign.Left, errorFont, paint);
                if (state.ErrorIsLink)
                {
                    float w = errorFont.MeasureText(line);
                    canvas.DrawRect(new SKRect(cardX + PadX, baseline + 2, cardX + PadX + w, baseline + 3), paint);
                }
                ly += errorLineHeight;
            }
        }

        return totalHeight;
    }

    private static void DrawCard(SKCanvas canvas, SKRect card)
    {
        using var paint = new SKPaint { IsAntialias = true };

        // Soft drop shadow
        paint.Color = new SKColor(0, 0, 0, 0x8C);
        paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5);
        var shadow = card;
        shadow.Offset(0, 2);
        canvas.DrawRoundRect(shadow, CardRadius, CardRadius, paint);
        paint.MaskFilter = null;

        // Skia multiplies shader output by the paint's alpha, so reset it to opaque.
        paint.Color = SKColors.White;
        paint.Shader = SKShader.CreateLinearGradient(
            new SKPoint(card.Left, card.Top), new SKPoint(card.Left, card.Bottom),
            new[] { new SKColor(0x1B, 0x1E, 0x22), new SKColor(0x13, 0x15, 0x18) }, SKShaderTileMode.Clamp);
        canvas.DrawRoundRect(card, CardRadius, CardRadius, paint);
        paint.Shader = null;

        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1;
        paint.Color = Hairline;
        var border = card;
        border.Inflate(-0.5f, -0.5f);
        canvas.DrawRoundRect(border, CardRadius - 0.5f, CardRadius - 0.5f, paint);
    }

    private static SKRect BackButtonRect(float cardLeft, float cardTop)
        => SKRect.Create(cardLeft + 6, cardTop + 5, 24, 24);

    private static void DrawIconButton(SKCanvas canvas, SKRect r, bool hover, Action<SKCanvas, float, float, SKColor> glyph, float radius = 7)
    {
        using var paint = new SKPaint { IsAntialias = true };
        if (hover)
        {
            paint.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x14);
            canvas.DrawRoundRect(r, radius, radius, paint);
        }
        glyph(canvas, r.MidX, r.MidY, hover ? TextPrimary : TextMuted);
    }

    private static void DrawGear(SKCanvas canvas, float cx, float cy, SKColor color)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, Color = color,
            StrokeWidth = 1.5f, StrokeCap = SKStrokeCap.Round,
        };
        canvas.DrawCircle(cx, cy, 4.6f, paint);
        canvas.DrawCircle(cx, cy, 1.8f, paint);
        // Teeth start inside the ring's stroke so they read as one gear, not a sun.
        paint.StrokeWidth = 2.6f;
        paint.StrokeCap = SKStrokeCap.Butt;
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
            canvas.DrawLine(cx + c * 4.8f, cy + s * 4.8f, cx + c * 7.2f, cy + s * 7.2f, paint);
        }
    }

    private static SKPaint GlyphPaint(SKColor color) => new()
    {
        IsAntialias = true, Style = SKPaintStyle.Stroke, Color = color,
        StrokeWidth = 1.5f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
    };

    private static void DrawClose(SKCanvas canvas, float cx, float cy, SKColor color)
    {
        using var paint = GlyphPaint(color);
        canvas.DrawLine(cx - 3.5f, cy - 3.5f, cx + 3.5f, cy + 3.5f, paint);
        canvas.DrawLine(cx + 3.5f, cy - 3.5f, cx - 3.5f, cy + 3.5f, paint);
    }

    // Chevrons pointing inward: shrink to the compact view.
    private static void DrawCollapse(SKCanvas canvas, float cx, float cy, SKColor color)
    {
        using var paint = GlyphPaint(color);
        canvas.DrawLine(cx - 3.5f, cy - 5, cx, cy - 2, paint);
        canvas.DrawLine(cx, cy - 2, cx + 3.5f, cy - 5, paint);
        canvas.DrawLine(cx - 3.5f, cy + 5, cx, cy + 2, paint);
        canvas.DrawLine(cx, cy + 2, cx + 3.5f, cy + 5, paint);
    }

    // Chevrons pointing outward: back to the full view.
    private static void DrawExpand(SKCanvas canvas, float cx, float cy, SKColor color)
    {
        using var paint = GlyphPaint(color);
        canvas.DrawLine(cx - 3, cy - 1.5f, cx, cy - 4.5f, paint);
        canvas.DrawLine(cx, cy - 4.5f, cx + 3, cy - 1.5f, paint);
        canvas.DrawLine(cx - 3, cy + 1.5f, cx, cy + 4.5f, paint);
        canvas.DrawLine(cx, cy + 4.5f, cx + 3, cy + 1.5f, paint);
    }

    private static void DrawBackChevron(SKCanvas canvas, float cx, float cy, SKColor color)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, Color = color,
            StrokeWidth = 1.8f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
        };
        canvas.DrawLine(cx + 2.5f, cy - 5, cx - 2.5f, cy, paint);
        canvas.DrawLine(cx - 2.5f, cy, cx + 2.5f, cy + 5, paint);
    }

    private static void DrawTitle(SKCanvas canvas, SKRect card)
    {
        var titleBold = Font(Bold, 14);
        var titleRegular = Font(Regular, 14);
        using var paint = new SKPaint { IsAntialias = true };

        float x = card.Left + PadX;
        float baseline = card.Top + 22;

        const string brand = "Claude Code";
        float brandW = titleBold.MeasureText(brand);
        paint.Shader = SKShader.CreateLinearGradient(
            new SKPoint(x, baseline - 11), new SKPoint(x, baseline),
            new[] { TitleOrangeLight, TitleOrange }, SKShaderTileMode.Clamp);
        canvas.DrawText(brand, x, baseline, SKTextAlign.Left, titleBold, paint);
        paint.Shader = null;

        paint.Color = TitleGray;
        canvas.DrawText("사용량", x + brandW + 4, baseline, SKTextAlign.Left, titleRegular, paint);
    }

    // ---- Settings page ----

    private const float ItemHeight = 34;
    private const float GroupGap = 6;

    private sealed record Item(string Id, string Label, ItemKind Kind);

    private enum ItemKind { Toggle, Poll, Action, DangerAction }

    private static readonly Item[] PreferenceItems =
    {
        new(HitId.Startup, "Windows 시작 시 실행", ItemKind.Toggle),
        new(HitId.Topmost, "항상 위에 표시", ItemKind.Toggle),
        new(HitId.CompactSetting, "간단히 보기", ItemKind.Toggle),
        new(HitId.Lock, "위치 고정", ItemKind.Toggle),
        new("poll", "갱신 주기", ItemKind.Poll),
    };

    private static readonly Item[] ActionItems =
    {
        new(HitId.Refresh, "지금 새로고침", ItemKind.Action),
        new(HitId.Relogin, "다시 로그인", ItemKind.Action),
    };

    private static float DrawSettings(SKCanvas? canvas, WidgetState state, List<HitRegion> hits)
    {
        float cardX = Margin, cardY = Margin;
        float cardRight = cardX + CardWidth;

        float group1Top = cardY + HeaderHeight;
        float group1Bottom = group1Top + PreferenceItems.Length * ItemHeight;
        float group2Top = group1Bottom + GroupGap;
        float group2Bottom = group2Top + ActionItems.Length * ItemHeight;
        float cardBottom = group2Bottom + Inset;
        float totalHeight = cardBottom + Margin;

        var back = BackButtonRect(cardX, cardY);
        hits.Add(new HitRegion(HitId.Back, back));

        var group1 = new SKRect(cardX + Inset, group1Top, cardRight - Inset, group1Bottom);
        var group2 = new SKRect(cardX + Inset, group2Top, cardRight - Inset, group2Bottom);
        LayoutGroup(group1, PreferenceItems, hits);
        LayoutGroup(group2, ActionItems, hits);

        if (canvas is null) return totalHeight;

        var card = new SKRect(cardX, cardY, cardRight, cardBottom);
        DrawCard(canvas, card);
        DrawIconButton(canvas, back, state.Hover == HitId.Back, DrawBackChevron);

        var titleFont = Font(Bold, 14);
        using (var paint = new SKPaint { IsAntialias = true, Color = TextPrimary })
            canvas.DrawText("설정", back.Right + 4, cardY + 22, SKTextAlign.Left, titleFont, paint);

        DrawGroup(canvas, group1, PreferenceItems, state);
        DrawGroup(canvas, group2, ActionItems, state);
        return totalHeight;
    }

    private static void LayoutGroup(SKRect group, Item[] items, List<HitRegion> hits)
    {
        for (int i = 0; i < items.Length; i++)
        {
            var itemRect = new SKRect(group.Left, group.Top + i * ItemHeight, group.Right, group.Top + (i + 1) * ItemHeight);
            if (items[i].Kind == ItemKind.Poll)
            {
                foreach (var (minutes, chip) in PollChips(itemRect))
                    hits.Add(new HitRegion(HitId.PollPrefix + minutes, chip));
            }
            else
            {
                hits.Add(new HitRegion(items[i].Id, itemRect));
            }
        }
    }

    private static IEnumerable<(int Minutes, SKRect Chip)> PollChips(SKRect item)
    {
        const float chipW = 32, chipH = 20, gap = 3;
        float x = item.Right - 8 - PollChoices.Length * chipW - (PollChoices.Length - 1) * gap;
        foreach (var m in PollChoices)
        {
            yield return (m, SKRect.Create(x, item.MidY - chipH / 2, chipW, chipH));
            x += chipW + gap;
        }
    }

    private static void DrawGroup(SKCanvas canvas, SKRect group, Item[] items, WidgetState state)
    {
        using var paint = new SKPaint { IsAntialias = true };
        var labelFont = Font(Regular, 12);
        var chipFont = Font(Regular, 10.5f);

        // Panel
        paint.Shader = SKShader.CreateLinearGradient(
            new SKPoint(group.Left, group.Top), new SKPoint(group.Left, group.Bottom),
            new[] { new SKColor(0x1E, 0x21, 0x25), new SKColor(0x18, 0x1A, 0x1E) }, SKShaderTileMode.Clamp);
        canvas.DrawRoundRect(group, RowRadius, RowRadius, paint);
        paint.Shader = null;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1;
        paint.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x12);
        var border = group;
        border.Inflate(-0.5f, -0.5f);
        canvas.DrawRoundRect(border, RowRadius, RowRadius, paint);
        paint.Style = SKPaintStyle.Fill;

        for (int i = 0; i < items.Length; i++)
        {
            var item = items[i];
            var r = new SKRect(group.Left, group.Top + i * ItemHeight, group.Right, group.Top + (i + 1) * ItemHeight);

            if (item.Kind != ItemKind.Poll && state.Hover == item.Id)
            {
                var h = r;
                h.Inflate(-3, -3);
                paint.Color = HoverFill;
                canvas.DrawRoundRect(h, 7, 7, paint);
            }

            if (i > 0)
            {
                paint.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x0F);
                canvas.DrawRect(new SKRect(r.Left + 12, r.Top, r.Right - 12, r.Top + 1), paint);
            }

            float baseline = r.MidY + 4;
            paint.Color = item.Kind == ItemKind.DangerAction ? Danger : TextPrimary;
            canvas.DrawText(item.Label, r.Left + 12, baseline, SKTextAlign.Left, labelFont, paint);

            switch (item.Kind)
            {
                case ItemKind.Toggle:
                    bool on = item.Id switch
                    {
                        HitId.Startup => state.StartupEnabled,
                        HitId.Topmost => state.Topmost,
                        HitId.Lock => state.Locked,
                        HitId.CompactSetting => state.Compact,
                        _ => false,
                    };
                    DrawToggle(canvas, new SKRect(r.Right - 12 - 30, r.MidY - 8.5f, r.Right - 12, r.MidY + 8.5f), on);
                    break;

                case ItemKind.Poll:
                    foreach (var (minutes, chip) in PollChips(r))
                    {
                        bool selected = minutes == state.PollMinutes;
                        bool hover = state.Hover == HitId.PollPrefix + minutes;
                        if (selected || hover)
                        {
                            paint.Color = selected ? Accent.WithAlpha(0x38) : HoverFill;
                            canvas.DrawRoundRect(chip, 6, 6, paint);
                        }
                        paint.Color = selected ? TitleOrangeLight : TextMuted;
                        canvas.DrawText($"{minutes}분", chip.MidX, chip.MidY + 3.7f, SKTextAlign.Center, chipFont, paint);
                    }
                    break;

                case ItemKind.Action:
                    DrawChevronRight(canvas, r.Right - 16, r.MidY, TextDim);
                    break;
            }
        }
    }

    private static void DrawToggle(SKCanvas canvas, SKRect r, bool on)
    {
        using var paint = new SKPaint { IsAntialias = true };
        float radius = r.Height / 2;
        paint.Color = on ? Accent : new SKColor(0x3A, 0x3F, 0x45);
        canvas.DrawRoundRect(r, radius, radius, paint);

        float knobX = on ? r.Right - radius : r.Left + radius;
        paint.Color = new SKColor(0, 0, 0, 0x40);
        canvas.DrawCircle(knobX, r.MidY + 0.6f, radius - 2, paint);
        paint.Color = on ? SKColors.White : new SKColor(0xC8, 0xCC, 0xD0);
        canvas.DrawCircle(knobX, r.MidY, radius - 2, paint);
    }

    private static void DrawChevronRight(SKCanvas canvas, float cx, float cy, SKColor color)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true, Style = SKPaintStyle.Stroke, Color = color,
            StrokeWidth = 1.5f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
        };
        canvas.DrawLine(cx - 2, cy - 4, cx + 2, cy, paint);
        canvas.DrawLine(cx + 2, cy, cx - 2, cy + 4, paint);
    }

    private static void DrawRow(SKCanvas canvas, SKRect r, UsageRow row)
    {
        var tone = ToneFor(row.Used);
        using var paint = new SKPaint { IsAntialias = true };

        // Row panel
        paint.Shader = SKShader.CreateLinearGradient(
            new SKPoint(r.Left, r.Top), new SKPoint(r.Left, r.Bottom),
            new[] { new SKColor(0x1E, 0x21, 0x25), new SKColor(0x18, 0x1A, 0x1E) }, SKShaderTileMode.Clamp);
        canvas.DrawRoundRect(r, RowRadius, RowRadius, paint);
        paint.Shader = null;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1;
        paint.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x12);
        var border = r;
        border.Inflate(-0.5f, -0.5f);
        canvas.DrawRoundRect(border, RowRadius, RowRadius, paint);
        paint.Style = SKPaintStyle.Fill;

        // Icon tile, vertically centred
        var tile = SKRect.Create(r.Left + 8, r.MidY - IconSize / 2, IconSize, IconSize);
        paint.Color = new SKColor(0x15, 0x18, 0x1B);
        canvas.DrawRoundRect(tile, 7, 7, paint);
        paint.Color = tone.Base.WithAlpha(0x14);
        canvas.DrawRoundRect(tile, 7, 7, paint);
        paint.Style = SKPaintStyle.Stroke;
        paint.Color = tone.Base.WithAlpha(0x22);
        var tileBorder = tile;
        tileBorder.Inflate(-0.5f, -0.5f);
        canvas.DrawRoundRect(tileBorder, 7, 7, paint);
        paint.Style = SKPaintStyle.Fill;
        DrawIcon(canvas, row.Icon, tile.MidX, tile.MidY, tone.Base, 0.8f);

        float textX = tile.Right + 10;
        float right = r.Right - 10;
        float baseline = r.Top + 21;

        // Label + short reset time ("현재 세션  리셋 18:50")
        var labelFont = Font(Bold, 12.5f);
        var resetFont = Font(Regular, 10.5f);
        paint.Color = TextPrimary;
        canvas.DrawText(row.Label, textX, baseline, SKTextAlign.Left, labelFont, paint);
        paint.Color = TextDim;
        canvas.DrawText(row.ResetText, textX + labelFont.MeasureText(row.Label) + 6, baseline, SKTextAlign.Left, resetFont, paint);

        // "91%", right aligned on the label baseline
        var numberFont = Font(Bold, 18);
        var percentFont = Font(Bold, 11);
        string number = Math.Round(row.Used).ToString();

        paint.Color = tone.Base;
        canvas.DrawText("%", right, baseline, SKTextAlign.Right, percentFont, paint);
        float numberRight = right - percentFont.MeasureText("%") - 1;
        paint.Shader = SKShader.CreateLinearGradient(
            new SKPoint(0, baseline - 14), new SKPoint(0, baseline),
            new[] { tone.Light, tone.Base }, SKShaderTileMode.Clamp);
        canvas.DrawText(number, numberRight, baseline, SKTextAlign.Right, numberFont, paint);
        paint.Shader = null;
        // Same wording as Claude's own usage page, so the number isn't read as "left".
        paint.Color = TextDim;
        canvas.DrawText("사용", numberRight - numberFont.MeasureText(number) - 4, baseline, SKTextAlign.Right, resetFont, paint);

        // Progress bar
        var track = new SKRect(textX, r.Top + 29, right, r.Top + 29 + BarHeight);
        float radius = BarHeight / 2;
        paint.Color = new SKColor(0x29, 0x2D, 0x32);
        canvas.DrawRoundRect(track, radius, radius, paint);

        float fillW = (float)(track.Width * Math.Clamp(row.Used, 0, 100) / 100);
        if (fillW > 0)
        {
            var fill = new SKRect(track.Left, track.Top, track.Left + Math.Max(fillW, BarHeight), track.Bottom);

            paint.Color = tone.Base.WithAlpha(0x55);
            paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 2.5f);
            canvas.DrawRoundRect(fill, radius, radius, paint);
            paint.MaskFilter = null;

            paint.Color = SKColors.White; // opaque, so the glow's alpha doesn't fade the gradient
            paint.Shader = SKShader.CreateLinearGradient(
                new SKPoint(fill.Left, 0), new SKPoint(fill.Right, 0),
                new[] { tone.Dark, tone.Light }, SKShaderTileMode.Clamp);
            canvas.DrawRoundRect(fill, radius, radius, paint);
            paint.Shader = null;

            // Top sheen
            paint.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, fill.Top), new SKPoint(0, fill.Bottom),
                new[] { new SKColor(0xFF, 0xFF, 0xFF, 0x40), new SKColor(0xFF, 0xFF, 0xFF, 0x00) },
                new[] { 0f, 0.6f }, SKShaderTileMode.Clamp);
            canvas.DrawRoundRect(fill, radius, radius, paint);
            paint.Shader = null;
        }
    }

    private static void DrawIcon(SKCanvas canvas, RowIcon icon, float cx, float cy, SKColor color, float scale)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.6f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            Color = color,
        };

        canvas.Save();
        canvas.Translate(cx, cy);
        canvas.Scale(scale);

        if (icon == RowIcon.Clock)
        {
            canvas.DrawCircle(0, 0, 7.5f, paint);
            canvas.DrawLine(0, 0, 0, -4.2f, paint);
            canvas.DrawLine(0, 0, 3, 2.2f, paint);
        }
        else
        {
            var body = new SKRect(-7.5f, -5.5f, 7.5f, 7.5f);
            canvas.DrawRoundRect(body, 2, 2, paint);
            canvas.DrawLine(body.Left, -1.5f, body.Right, -1.5f, paint);
            canvas.DrawLine(-3.5f, -8, -3.5f, -4, paint);
            canvas.DrawLine(3.5f, -8, 3.5f, -4, paint);

            paint.Style = SKPaintStyle.Fill;
            foreach (var (dx, dy) in new[] { (-3.5f, 2f), (0f, 2f), (3.5f, 2f), (-3.5f, 5f), (0f, 5f) })
                canvas.DrawCircle(dx, dy, 0.9f, paint);
        }

        canvas.Restore();
    }

    private static List<string> Wrap(string text, SKFont font, float maxWidth)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' '))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && font.MeasureText(candidate) > maxWidth)
            {
                lines.Add(line);
                line = word;
            }
            else
            {
                line = candidate;
            }
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }
}
