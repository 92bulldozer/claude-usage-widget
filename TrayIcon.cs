using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using SkiaSharp;

namespace ClaudeUsageWidget;

/// <summary>
/// System tray (notification area) icon. The icon itself shows the used
/// 5-hour percentage; left-click toggles the widget, right-click opens the menu.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly MainWindow _window;
    private readonly ToolStripMenuItem _showItem;
    private readonly ToolStripMenuItem _topmostItem;
    private readonly ToolStripMenuItem _startupItem;
    private IntPtr _hIcon;
    private string? _iconKey;

    public TrayIcon(MainWindow window, AppSettings settings)
    {
        _window = window;

        _showItem = new ToolStripMenuItem("위젯 보이기", null, (_, _) => ToggleWidget()) { Checked = settings.Visible };
        _topmostItem = new ToolStripMenuItem("항상 위에 표시", null, (_, _) =>
        {
            _topmostItem!.Checked = !_topmostItem.Checked;
            _window.SetTopmost(_topmostItem.Checked);
        }) { Checked = settings.Topmost };
        _startupItem = new ToolStripMenuItem("Windows 시작 시 실행", null, (_, _) =>
        {
            StartupHelper.SetEnabled(!_startupItem!.Checked);
            _startupItem.Checked = StartupHelper.IsEnabled();
        }) { Checked = StartupHelper.IsEnabled() };
        var compactItem = new ToolStripMenuItem("간단히 보기", null, (_, _) => _window.SetCompact(!settings.Compact))
        {
            Checked = settings.Compact,
        };

        var menu = new ContextMenuStrip();
        // The widget's settings page can change these too, so re-read on open.
        menu.Opening += (_, _) =>
        {
            _showItem.Checked = _window.IsVisible;
            compactItem.Checked = settings.Compact;
            _topmostItem.Checked = _window.Topmost;
            _startupItem.Checked = StartupHelper.IsEnabled();
        };
        menu.Items.Add(_showItem);
        menu.Items.Add(compactItem);
        menu.Items.Add("지금 새로고침", null, async (_, _) => await _window.RefreshAsync());
        menu.Items.Add("로그인", null, async (_, _) => await _window.LoginAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_topmostItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) =>
        {
            _window.SavePosition();
            System.Windows.Application.Current.Shutdown();
        });

        _icon = new NotifyIcon { ContextMenuStrip = menu };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ToggleWidget();
        };
        _window.IsVisibleChanged += (_, _) =>
        {
            _showItem.Checked = _window.IsVisible;
            // The close button only hides the widget; tell the user once where it went.
            if (!_window.IsVisible && !settings.CloseHintShown)
            {
                settings.CloseHintShown = true;
                settings.Save();
                _icon!.ShowBalloonTip(4000, "Claude Code 사용량",
                    "위젯을 숨겼어요. 트레이 아이콘을 클릭하면 다시 열리고, 완전히 끄려면 우클릭 → 종료.", ToolTipIcon.Info);
            }
        };

        Update();
        _icon.Visible = true;
    }

    public void Update()
    {
        var s = _window.Snapshot;
        var tip = new StringBuilder("Claude Code 사용량");
        if (s?.FiveHour is { } five) tip.Append($"\n세션: {Math.Round(five.Used)}% 사용");
        if (s?.SevenDay is { } week) tip.Append($"\n주간: {Math.Round(week.Used)}% 사용");
        if (_window.LastError is { } err) tip.Append($"\n{err}");
        var text = tip.ToString();
        _icon.Text = text.Length > 127 ? text[..127] : text;

        SetIcon(s?.FiveHour?.Used);
    }

    private void ToggleWidget() => _window.SetWidgetVisible(!_window.IsVisible);

    private void SetIcon(double? used)
    {
        // Re-encoding the icon is the priciest thing a poll does; skip it when unchanged.
        var key = used is { } k ? Math.Round(k).ToString() : "?";
        if (key == _iconKey) return;
        _iconKey = key;

        const int size = 32;
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using var paint = new SKPaint { IsAntialias = true };
        paint.Color = used is null
            ? new SKColor(0x70, 0x70, 0x78)
            : used < 50 ? new SKColor(0x3F, 0xA8, 0x62)
            : used < 80 ? new SKColor(0xD8, 0x96, 0x1E)
            : new SKColor(0xD6, 0x45, 0x3A);
        canvas.DrawRoundRect(new SKRect(0, 0, size, size), 7, 7, paint);

        var label = used is { } r ? Math.Round(r).ToString() : "?";
        using var typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold);
        using var font = new SKFont(typeface, label.Length >= 3 ? 13f : 19f) { Edging = SKFontEdging.Antialias };
        font.MeasureText(label, out var bounds);
        paint.Color = SKColors.White;
        canvas.DrawText(label, size / 2f, size / 2f - bounds.MidY, SKTextAlign.Center, font, paint);

        // Skia pixels -> GDI bitmap -> HICON for NotifyIcon.
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = data.AsStream();
        using var bmp = new Bitmap(stream);

        var handle = bmp.GetHicon();
        _icon.Icon = Icon.FromHandle(handle);
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        _hIcon = handle;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
