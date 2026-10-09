using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Security.Principal;
using System.Threading;
using HardwareScope.Core;
using Forms = System.Windows.Forms;

namespace HardwareScope.Desktop;

public partial class App : System.Windows.Application
{
    private MonitorHost? host;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private MainWindow? window;
    private bool exiting;
    private Mutex? singleInstance;
    private bool ownsInstance;
    private ThemeManager? themes;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var verification = e.Args.FirstOrDefault(a => a.StartsWith("--verify-themes=", StringComparison.OrdinalIgnoreCase));
        if (verification is not null) { Shutdown(ThemeVerification.Run(Path.GetFullPath(verification.Split('=', 2)[1]))); return; }
        var smoke = e.Args.Contains("--smoke-ui", StringComparer.OrdinalIgnoreCase);
        singleInstance = new Mutex(false, "Local\\HardwareScope-" + WindowsIdentity.GetCurrent().User!.Value + (smoke ? "-smoke-" + Guid.NewGuid() : ""));
        try { ownsInstance = singleInstance.WaitOne(e.Args.Contains("--elevated-handoff") ? TimeSpan.FromSeconds(15) : TimeSpan.Zero); }
        catch (AbandonedMutexException) { ownsInstance = true; }
        if (!ownsInstance)
        {
            if (e.Args.Contains("--smoke-ui")) { Shutdown(2); return; }
            MessageBox.Show("HardwareScope 已在后台运行。\n双击任务栏托盘中的 HardwareScope 图标即可打开窗口。", "HardwareScope", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        try { host = new MonitorHost(smoke ? Path.Combine(Path.GetTempPath(), "HardwareScope-ui-smoke", Guid.NewGuid().ToString()) : null); }
        catch (Exception ex)
        {
            MessageBox.Show($"无法读取监控配置或连接凭据：{ex.Message}\n\n原有文件已保留，请检查本地应用数据目录中的 HardwareScope 文件夹。", "HardwareScope 无法启动", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        var themeArgument = e.Args.FirstOrDefault(a => a.StartsWith("--smoke-theme=", StringComparison.OrdinalIgnoreCase));
        ThemePreference? preview = smoke && themeArgument is not null && Enum.TryParse<ThemePreference>(themeArgument.Split('=', 2)[1], true, out var preference) ? preference : null;
        themes = new ThemeManager(smoke, preview);
        window = new MainWindow(host, ExitAsync, themes);
        if (smoke)
        {
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000; window.Top = -20000;
            if (e.Args.Contains("--smoke-small")) { window.Width = 960; window.Height = 620; }
        }
        MainWindow = window;
        window.Closing += (_, args) =>
        {
            if (exiting) return;
            args.Cancel = true;
            if (host.Settings.MinimizeToTray) window.Hide();
            else _ = ExitAsync();
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 HardwareScope", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add("退出并停止监控", null, (_, _) => Dispatcher.InvokeAsync(ExitAsync));
        using (var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream)
            trayIcon = new System.Drawing.Icon(iconStream);
        tray = new Forms.NotifyIcon
        {
            Icon = trayIcon,
            Text = "HardwareScope · 后台硬件监控",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) window.Show();
        try { await host.StartAsync(); }
        catch (Exception ex) { window.SetStartupError("监控启动失败：" + ex.Message); ShowWindow(); }
        if (e.Args.Contains("--smoke-ui", StringComparer.OrdinalIgnoreCase))
        {
            if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) window.Show();
            var pageArg = e.Args.FirstOrDefault(a => a.StartsWith("--smoke-page=", StringComparison.OrdinalIgnoreCase));
            if (pageArg is not null && int.TryParse(pageArg.Split('=', 2)[1], out var page)) window.SetSmokePage(page);
            var durationArg = e.Args.FirstOrDefault(a => a.StartsWith("--smoke-duration=", StringComparison.OrdinalIgnoreCase));
            var seconds = durationArg is not null && int.TryParse(durationArg.Split('=', 2)[1], out var requestedSeconds) && requestedSeconds > 0 ? requestedSeconds : 6;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                var outputArg = e.Args.FirstOrDefault(a => a.StartsWith("--smoke-output=", StringComparison.OrdinalIgnoreCase));
                var path = outputArg is null ? Path.Combine(AppContext.BaseDirectory, "desktop-smoke.png") : Path.GetFullPath(outputArg.Split('=', 2)[1]);
                if (window.IsVisible && window.Content is FrameworkElement element && element.ActualWidth > 0 && element.ActualHeight > 0)
                {
                    var scrollArg = e.Args.FirstOrDefault(a => a.StartsWith("--smoke-scroll=", StringComparison.OrdinalIgnoreCase));
                    if (scrollArg is not null && double.TryParse(scrollArg.Split('=', 2)[1], out var offset)) { window.SetSmokeScroll(offset); window.UpdateLayout(); }
                    var dpi = VisualTreeHelper.GetDpi(element);
                    var bitmap = new RenderTargetBitmap((int)(element.ActualWidth * dpi.DpiScaleX), (int)(element.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                    bitmap.Render(element);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var file = File.Create(path);
                    encoder.Save(file);
                }
                await ExitAsync();
            };
            timer.Start();
        }
    }

    private void ShowWindow()
    {
        if (window is null) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private async Task ExitAsync()
    {
        if (exiting) return;
        exiting = true;
        if (tray is not null) { tray.Visible = false; tray.Dispose(); tray = null; }
        trayIcon?.Dispose();
        trayIcon = null;
        window?.Detach();
        themes?.Dispose();
        if (host is not null) await host.DisposeAsync();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsInstance) singleInstance?.ReleaseMutex();
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
