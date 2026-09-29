using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;

namespace ClaudeUsageWidget;

public partial class MainWindow : Window
{
    private static readonly CultureInfo Ko = CultureInfo.GetCultureInfo("ko-KR");

    private readonly AppSettings _settings;
    private readonly UsageService _service = new();
    private readonly DispatcherTimer _pollTimer = new();
    private readonly DispatcherTimer _credentialsDebounce = new() { Interval = TimeSpan.FromSeconds(2) };
    private FileSystemWatcher? _credentialsWatcher;
    private bool _fetching;
    private DateTimeOffset _lastFetchAt = DateTimeOffset.MinValue;
    private WidgetState _state = new();
    private readonly List<HitRegion> _hits = new();
    private WidgetPage _page = WidgetPage.Usage;
    private string? _hover;
    private string? _errorOverride;
    private WriteableBitmap? _bitmap;
    // Registry read; cached because Render runs on every hover change.
    private bool _startupEnabled = StartupHelper.IsEnabled();

    public UsageSnapshot? Snapshot { get; private set; }
    public string? LastError { get; private set; }
    public bool NeedsLogin { get; private set; }

    /// <summary>Raised after every fetch attempt so the tray icon can follow.</summary>
    public event Action? Updated;

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        Width = WidgetRenderer.Width;
        _settings = settings;
        Snapshot = settings.LastSnapshot;
        Topmost = settings.Topmost;

        var area = SystemParameters.WorkArea;
        Left = settings.Left ?? area.Right - Width - 24;
        Top = settings.Top ?? area.Top + 24;
        if (Left < SystemParameters.VirtualScreenLeft || Left > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40
            || Top < SystemParameters.VirtualScreenTop || Top > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40)
        {
            Left = area.Right - Width - 24;
            Top = area.Top + 24;
        }

        _pollTimer.Tick += async (_, _) => await RefreshAsync();
        SourceInitialized += (_, _) => HideFromAltTab();
        // Nothing is painted while hidden; catch up when shown again.
        IsVisibleChanged += (_, _) => { if (IsVisible) Render(); };
        Render();
    }

    public void Start()
    {
        WatchCredentials();
        _ = RefreshAsync();
    }

    // Refetch as soon as Claude Code writes a new token (login or refresh),
    // instead of waiting out the poll/back-off interval.
    private void WatchCredentials()
    {
        _credentialsDebounce.Tick += async (_, _) =>
        {
            _credentialsDebounce.Stop();
            // Our own background refresh also rewrites the file; the usage API is
            // rate-limited, so don't refetch right after a fetch that just happened.
            if (LastError is null && DateTimeOffset.UtcNow - _lastFetchAt < TimeSpan.FromSeconds(30)) return;
            await RefreshAsync();
        };

        var dir = UsageService.CredentialsDir;
        if (!Directory.Exists(dir)) return;

        _credentialsWatcher = new FileSystemWatcher(dir, UsageService.CredentialsFileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        FileSystemEventHandler onChange = (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _credentialsDebounce.Stop();
            _credentialsDebounce.Start();
        });
        _credentialsWatcher.Changed += onChange;
        _credentialsWatcher.Created += onChange;
        _credentialsWatcher.Renamed += (s, e) => onChange(s, e);
        _credentialsWatcher.EnableRaisingEvents = true;
    }

    public async Task RefreshAsync()
    {
        if (_fetching) return;
        _fetching = true;
        _pollTimer.Stop();

        var next = TimeSpan.FromMinutes(Math.Max(1, _settings.PollMinutes));
        try
        {
            if (CliAuth.NeedsRefresh())
                await CliAuth.RunStatusAsync();

            var result = await _service.FetchAsync();
            if (result.TokenExpired)
            {
                // Server rejected a token that looked valid locally: let the CLI refresh, retry once.
                await CliAuth.RunStatusAsync();
                result = await _service.FetchAsync();
            }
            _lastFetchAt = DateTimeOffset.UtcNow;
            NeedsLogin = result.NeedsLogin;

            if (result.Snapshot is not null)
            {
                Snapshot = result.Snapshot;
                LastError = null;
                _settings.LastSnapshot = result.Snapshot;
                _settings.Save();
            }
            else
            {
                LastError = result.Error;
                if (result.RetryAfter is { } retry && retry > next)
                    next = retry;
            }
        }
        finally
        {
            _fetching = false;
            _pollTimer.Interval = next;
            _pollTimer.Start();
            Render();
            Updated?.Invoke();
        }
    }

    public void SetTopmost(bool value)
    {
        Topmost = value;
        _settings.Topmost = value;
        _settings.Save();
    }

    public void SetWidgetVisible(bool visible)
    {
        if (visible) Show(); else Hide();
        _settings.Visible = visible;
        _settings.Save();
    }

    private void Render()
    {
        var rows = new List<UsageRow>();
        if (Snapshot is { } s)
        {
            bool compact = _settings.Compact;
            AddRow(rows, compact ? "5시간" : "5시간 세션", s.FiveHour, RowIcon.Clock);
            AddRow(rows, "주간", s.SevenDay, RowIcon.Calendar);
            AddRow(rows, compact ? "Opus" : "주간 Opus", s.SevenDayOpus, RowIcon.Calendar);
            AddRow(rows, compact ? "Sonnet" : "주간 Sonnet", s.SevenDaySonnet, RowIcon.Calendar);
        }

        var error = _errorOverride ?? LastError ?? (Snapshot is null ? "불러오는 중…" : null);
        _state = new WidgetState
        {
            Page = _page,
            Compact = _settings.Compact,
            Hover = _hover,
            Rows = rows,
            Error = error,
            ErrorIsLink = NeedsLogin && _errorOverride is null,
            StartupEnabled = _startupEnabled,
            Topmost = Topmost,
            Locked = _settings.Locked,
            PollMinutes = _settings.PollMinutes,
        };

        // Keep the right edge in place when the width changes (compact <-> full),
        // since the widget usually sits at the right side of the screen.
        double width = WidgetRenderer.WidthFor(_state);
        if (Math.Abs(width - Width) > 0.5)
        {
            Left += Width - width;
            Width = width;
        }
        Height = Math.Ceiling(WidgetRenderer.Draw(null, _state, _hits));
        if (IsVisible) Paint();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Render();
    }

    /// <summary>Renders the widget with Skia straight into the WPF bitmap's back buffer.</summary>
    private void Paint()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int pw = (int)Math.Ceiling(Width * dpi.DpiScaleX);
        int ph = (int)Math.Ceiling(Height * dpi.DpiScaleY);
        if (pw <= 0 || ph <= 0) return;

        if (_bitmap is null || _bitmap.PixelWidth != pw || _bitmap.PixelHeight != ph)
        {
            _bitmap = new WriteableBitmap(pw, ph, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32, null);
            Surface.Source = _bitmap;
        }

        _bitmap.Lock();
        try
        {
            var info = new SKImageInfo(pw, ph, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info, _bitmap.BackBuffer, _bitmap.BackBufferStride);
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)dpi.DpiScaleX, (float)dpi.DpiScaleY);
            WidgetRenderer.Draw(canvas, _state, _hits);
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, pw, ph));
        }
        finally
        {
            _bitmap.Unlock();
        }
    }

    private string? HitTest(MouseEventArgs e)
    {
        var p = e.GetPosition(Surface);
        foreach (var hit in _hits)
            if (hit.Rect.Contains((float)p.X, (float)p.Y))
                return hit.Id;
        return null;
    }

    private void SetHover(string? id)
    {
        if (id == _hover) return;
        _hover = id;
        Cursor = id is null ? null : Cursors.Hand;
        Render();
    }

    private void OnMouseMove(object sender, MouseEventArgs e) => SetHover(HitTest(e));

    private void OnMouseLeave(object sender, MouseEventArgs e) => SetHover(null);

    private async void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        if (HitTest(e) is { } id)
        {
            await HandleClickAsync(id);
            return;
        }

        if (_settings.Locked) return;
        DragMove();
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save();
    }

    private async Task HandleClickAsync(string id)
    {
        switch (id)
        {
            case HitId.Settings:
                ShowPage(WidgetPage.Settings);
                break;
            case HitId.Back:
                ShowPage(WidgetPage.Usage);
                break;
            case HitId.Startup:
                StartupHelper.SetEnabled(!_startupEnabled);
                _startupEnabled = StartupHelper.IsEnabled();
                Render();
                break;
            case HitId.Topmost:
                SetTopmost(!Topmost);
                Render();
                break;
            case HitId.ToggleCompact:
            case HitId.CompactSetting:
                SetCompact(!_settings.Compact);
                break;
            case HitId.Close:
                ShowPage(WidgetPage.Usage);
                SetWidgetVisible(false);
                break;
            case HitId.Lock:
                _settings.Locked = !_settings.Locked;
                _settings.Save();
                Render();
                break;
            case HitId.Refresh:
                ShowPage(WidgetPage.Usage);
                await RefreshAsync();
                break;
            case HitId.Login:
            case HitId.Relogin:
                await LoginAsync();
                break;
            default:
                if (id.StartsWith(HitId.PollPrefix) && int.TryParse(id[HitId.PollPrefix.Length..], out var minutes))
                    SetPollMinutes(minutes);
                break;
        }
    }

    public void SetCompact(bool compact)
    {
        _settings.Compact = compact;
        _hover = null;
        Cursor = null;
        Render();
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save();
    }

    private void ShowPage(WidgetPage page)
    {
        _page = page;
        // The tray menu can change this too; re-read when the settings page opens.
        if (page == WidgetPage.Settings) _startupEnabled = StartupHelper.IsEnabled();
        _hover = null;
        Cursor = null;
        Render();
    }

    private void SetPollMinutes(int minutes)
    {
        _settings.PollMinutes = minutes;
        _settings.Save();
        // Apply now unless a fetch is running (it restarts the timer itself when done).
        if (!_fetching)
        {
            _pollTimer.Stop();
            _pollTimer.Interval = TimeSpan.FromMinutes(minutes);
            _pollTimer.Start();
        }
        Render();
    }

    public async Task LoginAsync()
    {
        ShowPage(WidgetPage.Usage);
        _errorOverride = "브라우저에서 로그인을 완료하세요…";
        Render();
        await CliAuth.LoginInBackgroundAsync();
        _errorOverride = null;
        await RefreshAsync();
    }

    private static void AddRow(List<UsageRow> rows, string label, UsageWindow? w, RowIcon icon)
    {
        if (w is null) return;
        rows.Add(new UsageRow(label, w.Remaining, FormatReset(w.ResetsAt, weekly: icon == RowIcon.Calendar), icon));
    }

    private static string FormatReset(DateTimeOffset? resetsAt, bool weekly)
    {
        if (resetsAt is not { } at) return "";
        var local = at.ToLocalTime();
        return weekly ? $"리셋 {local.ToString("M/d(ddd)", Ko)}" : $"리셋 {local:HH:mm}";
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void OnHideClick(object sender, RoutedEventArgs e) => SetWidgetVisible(false);

    // Tool-window style keeps the widget out of Alt+Tab.
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private void HideFromAltTab()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
    }
}
