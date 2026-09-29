using System.Threading;
using System.Windows;

namespace ClaudeUsageWidget;

public partial class App : Application
{
    private Mutex? _mutex;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "ClaudeUsageWidget_SingleInstance", out bool created);
        if (!created)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // A small static widget gains nothing from the GPU, and hardware rendering
        // loads the whole graphics driver stack (~200 MB on Intel iGPUs).
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        StartupHelper.RepairPath();
        var settings = AppSettings.Load();
        var window = new MainWindow(settings);
        _tray = new TrayIcon(window, settings);
        window.Updated += _tray.Update;
        // Windows sign-out/shutdown ends the app without going through the tray menu.
        SessionEnding += (_, _) => window.SavePosition();

        if (settings.Visible)
            window.Show();

        window.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
