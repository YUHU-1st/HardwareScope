using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace HardwareScope.Desktop;

public sealed partial class MainWindow
{
    private void BuildConnection()
    {
        var panel = new StackPanel();
        var settings = host.Settings;
        var enabled = new CheckBox { Content = "启用局域网连接（HTTPS）", IsChecked = settings.RemoteEnabled };
        var control = new CheckBox { Content = "允许手机设置优先级、结束进程及修改设置", IsChecked = settings.RemoteControlEnabled };
        var bluetooth = new CheckBox { Content = "启用蓝牙连接", IsChecked = settings.BluetoothEnabled };
        var port = new TextBox { Text = settings.Port.ToString(), Width = 145, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 5, 0, 12) };
        var feedback = Text("", 12, false, AccentColor);
        var apply = Button("保存连接设置", async () =>
        {
            if (!int.TryParse(port.Text, out var number) || number is < 1024 or > 65535) { feedback.Text = "请输入 1024–65535 之间的端口。"; return; }
            try
            {
                await host.SaveSettingsAsync(host.Settings with { RemoteEnabled = enabled.IsChecked == true, RemoteControlEnabled = control.IsChecked == true, BluetoothEnabled = bluetooth.IsChecked == true, Port = number });
                feedback.Text = "连接设置已保存。下方显示实际服务状态。";
                RefreshPage();
            }
            catch (Exception ex) { feedback.Text = "保存失败：" + ex.Message; }
        });
        apply.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(Card(Stack(Text("连接与控制权限", 16, true), Hint("启用局域网或蓝牙后，手机选择本电脑并输入六位 PIN 即可配对。远程控制权限可随时关闭。"), enabled, bluetooth, control, Hint("局域网服务端口"), port, apply, feedback)));
        var addresses = new ListBox { MinHeight = 50, MaxHeight = 180, Margin = new Thickness(0, 10, 0, 10) };
        var connectionState = Hint("");
        var bluetoothStatus = Hint("");
        var copyAddress = Button("复制局域网地址", () =>
        {
            var value = addresses.SelectedItem as string;
            if (value is not null) { Clipboard.SetText(value); feedback.Text = "局域网地址已复制。"; }
            return Task.CompletedTask;
        });
        copyAddress.HorizontalAlignment = HorizontalAlignment.Left;
        var pin = Text("", 36, true, AccentColor);
        pin.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        pin.Margin = new Thickness(0, 12, 0, 10);
        var pinState = Hint("");
        if (host.GetPairingInfo().Pin is null && host.GetPairingInfo().LockedUntil is null) host.CreatePairingPin();
        var pinButtons = new WrapPanel();
        var generate = Button("生成新 PIN", () =>
        {
            host.CreatePairingPin();
            feedback.Text = "已生成新的六位 PIN，有效期五分钟。";
            RefreshPage();
            return Task.CompletedTask;
        });
        generate.SetResourceReference(StyleProperty, "PrimaryButton");
        generate.Margin = new Thickness(0, 0, 9, 8);
        pinButtons.Children.Add(generate);
        var copyPin = Button("复制 PIN", () =>
        {
            var currentPin = host.GetPairingInfo().Pin;
            if (currentPin is not null) { Clipboard.SetText(currentPin); feedback.Text = "六位 PIN 已复制。"; }
            return Task.CompletedTask;
        });
        copyPin.Margin = new Thickness(0, 0, 9, 8);
        pinButtons.Children.Add(copyPin);
        var revoke = Button("撤销手机授权", () =>
        {
            if (MessageBox.Show(this, "撤销后，所有已配对手机都需要使用新 PIN 重新配对。继续？", "撤销手机授权", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            {
                host.RotateRemoteToken();
                feedback.Text = "手机授权已撤销。请生成新 PIN 重新配对。";
                RefreshPage();
            }
            return Task.CompletedTask;
        });
        revoke.Margin = new Thickness(0, 0, 0, 8);
        pinButtons.Children.Add(revoke);
        panel.Children.Insert(0, Card(Stack(Text("六位 PIN 配对", 18, true), Hint("启用局域网或蓝牙后，在手机端选择本电脑并输入下方 PIN。"), pin, pinState, pinButtons, Hint("PIN 五分钟内有效，配对成功后失效；手机安全保存授权，后续可自动重连。"))));
        panel.Children.Add(Card(Stack(Text("电脑连接信息", 16, true), connectionState, Hint("手机会查找同一局域网内的电脑；下方地址用于检查网络连接。"), addresses, copyAddress, bluetoothStatus)));
        panel.Children.Add(Card(Stack(Text("如何连接", 16, true), Hint("局域网：手机与电脑连接同一个 Wi-Fi / 局域网，在安卓端查找并选择本电脑，输入六位 PIN。Windows 防火墙询问时允许可信的专用网络。\n蓝牙：先在 Windows 与 Android 系统设置中完成配对，再在安卓端选择本电脑并输入六位 PIN。\n配对完成后可实时查看硬件与进程；允许手机控制后还可修改设置、调整优先级或结束进程。"))));
        panel.Children.Add(Card(Stack(Text("vivo 跨网络连接", 16, true), Hint("公开 SDK 的账号发现、电脑到手机通道及第三方应用授权仍需官方确认。本版提供局域网与蓝牙；vivo SDK 调研结论随交付文档提供。"))));
        string? addressKey = null;
        refresh.Add(() =>
        {
            var info = host.GetConnectionInfo();
            var key = string.Join("|", info.Addresses);
            if (addressKey != key)
            {
                var selection = addresses.SelectedItem as string;
                addressKey = key;
                addresses.ItemsSource = info.Addresses.Length > 0 ? info.Addresses : ["未发现可用的局域网 IPv4 地址。"];
                addresses.SelectedItem = selection is not null && info.Addresses.Contains(selection) ? selection : info.Addresses.FirstOrDefault();
            }
            connectionState.Text = $"局域网服务：{(info.RemoteEnabled ? "运行中" : "未启用 / 未成功启动")}  ·  远程控制：{(info.RemoteControlEnabled ? "允许" : "关闭")}";
            bluetoothStatus.Text = "\n蓝牙：" + info.BluetoothStatus;
            var pairing = host.GetPairingInfo();
            var locked = pairing.LockedUntil is { } until && until > DateTimeOffset.UtcNow;
            pin.Text = pairing.Pin is { } code ? string.Join(" ", code.ToCharArray()) : "— — — — — —";
            pinState.Text = locked ? "尝试次数过多，请稍后重新配对。" : pairing.ExpiresAt is { } expires && pairing.Pin is not null
                ? $"剩余有效时间：{Math.Max(0, (int)(expires - DateTimeOffset.UtcNow).TotalMinutes):0} 分 {Math.Max(0, (int)(expires - DateTimeOffset.UtcNow).TotalSeconds) % 60:00} 秒"
                : "PIN 已使用或过期，点击“生成新 PIN”可配对另一台手机。";
            generate.IsEnabled = !locked;
            copyPin.IsEnabled = pairing.Pin is not null && !locked;
            copyAddress.IsEnabled = info.Addresses.Length > 0;
        });
        content.Children.Add(Scroll(panel));
    }

    private sealed record IntervalChoice(int Value, string Name) { public override string ToString() => Name; }

    private void BuildSettings()
    {
        var panel = new StackPanel();
        var appearance = ThemePicker();
        appearance.Width = 240;
        appearance.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(Card(Stack(Text("界面主题", 18, true), Hint("浅色采用冷白仪表面板，深色采用青蓝终端配色。选择后立即生效，并同步到文件占用模块。"), appearance, Hint("跟随系统会响应 Windows 应用主题变化；手动选择会持续保留。"))));
        var settings = host.Settings;
        var interval = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 9, 0, 15), ItemsSource = new[] { new IntervalChoice(1000, "每 1 秒"), new IntervalChoice(2000, "每 2 秒（默认）"), new IntervalChoice(5000, "每 5 秒"), new IntervalChoice(10000, "每 10 秒") }, SelectedValuePath = "Value", SelectedValue = settings.SamplingIntervalMs };
        var startup = new CheckBox { Content = "登录 Windows 后自动启动到托盘", IsChecked = settings.StartWithWindows };
        var tray = new CheckBox { Content = "关闭窗口后继续在托盘中运行", IsChecked = settings.MinimizeToTray };
        var feedback = Text("", 12, false, AccentColor);
        var apply = Button("保存设置", async () =>
        {
            try
            {
                await host.SaveSettingsAsync(host.Settings with { SamplingIntervalMs = (interval.SelectedItem as IntervalChoice)?.Value ?? settings.SamplingIntervalMs, StartWithWindows = startup.IsChecked == true, MinimizeToTray = tray.IsChecked == true });
                feedback.Text = "设置已保存，将在下次采样生效。";
            }
            catch (Exception ex) { feedback.Text = "保存失败：" + ex.Message; }
        });
        apply.Margin = new Thickness(0, 13, 0, 8);
        apply.SetResourceReference(StyleProperty, "PrimaryButton");
        apply.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(Card(Stack(Text("采样与启动", 16, true), Hint("较长的采样间隔可降低监控开销。曲线只在新采样到达时绘制。"), interval, startup, tray, apply, feedback)));
        panel.Children.Add(Card(Stack(Text("后台运行", 16, true), Hint("关闭窗口后，监控与手机连接在当前登录用户的后台应用中继续运行。双击任务栏托盘图标重新打开，或右键选择“退出并停止监控”。注销 Windows 或退出本程序会停止监控。"))));
        var privilege = Hint("");
        var elevation = Button("以管理员身份重新打开", () => RestartElevatedAsync(feedback));
        elevation.HorizontalAlignment = HorizontalAlignment.Left;
        elevation.Margin = new Thickness(0, 13, 0, 0);
        panel.Children.Add(Card(Stack(Text("传感器访问权限", 16, true), privilege, elevation)));
        var diagnostics = Hint("");
        panel.Children.Add(Card(Stack(Text("运行开销", 16, true), diagnostics, Hint("这些数值包含本软件的采样与图形界面开销，可在实际电脑上持续观察。进程表和图线都不使用动画刷新。"))));
        panel.Children.Add(Card(Stack(Text("HardwareScope " + typeof(MainWindow).Assembly.GetName().Version!.ToString(3), 16, true), Hint("Windows 原生监控 · 安卓远程客户端\n设置保存于当前用户的本地应用数据目录，保存前保留上一版配置。传感器测量来自硬件监控库、Windows 计数器及显卡驱动；不可用指标会显示原因。"))));
        refresh.Add(() =>
        {
            privilege.Text = snapshot.IsAdministrator ? "当前以管理员权限运行，可尝试访问更多主板、CPU 温度及功耗传感器。" : "当前以标准权限运行。部分主板、CPU 温度及功耗需要管理员权限；是否可用仍取决于硬件和驱动。";
            elevation.IsEnabled = !snapshot.IsAdministrator;
            diagnostics.Text = $"本次采样：{snapshot.Diagnostics.SampleDurationMs:0.0} ms\n监控 CPU 占用：{snapshot.Diagnostics.MonitorCpuPercent:0.00}%\n监控内存：{snapshot.Diagnostics.MonitorMemoryMb:0.0} MB\n采样间隔：{snapshot.Diagnostics.IntervalMs / 1000d:0.#} 秒";
        });
        content.Children.Add(Scroll(panel));
    }

    private async Task RestartElevatedAsync(TextBlock feedback)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--elevated-handoff") { UseShellExecute = true, Verb = "runas" });
            await exit();
        }
        catch (System.ComponentModel.Win32Exception ex) { feedback.Text = ex.NativeErrorCode == 1223 ? "已取消管理员授权，当前监控继续运行。" : "无法重新启动：" + ex.Message; }
    }
}
