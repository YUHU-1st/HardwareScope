using System.Windows;
using System.Windows.Controls;
using HardwareScope.Core;

namespace HardwareScope.Desktop;

public sealed partial class MainWindow
{
    private Border BuildTemperatureOverview()
    {
        var heading = Text("全部硬件温度", 18, true);
        var counts = Hint("正在读取温度传感器…");
        var permission = Hint("");
        permission.Margin = new Thickness(0, 8, 0, 8);
        var feedback = Text("", 12, false, AccentColor);
        var elevation = Button("以管理员身份重新打开", () => RestartElevatedAsync(feedback));
        elevation.HorizontalAlignment = HorizontalAlignment.Left;
        elevation.Margin = new Thickness(0, 0, 0, 12);
        var devicesPanel = new WrapPanel();
        var section = Card(Stack(heading, counts, permission, elevation, feedback, devicesPanel));
        var renders = new Dictionary<string, Action<HardwareDevice>>();
        string[] categoryOrder = ["CPU", "GPU", "Motherboard", "NPU", "Memory", "VRAM", "Storage", "Network"];
        string? inventoryKey = null;

        void ResizeCards()
        {
            var width = Math.Max(0, devicesPanel.ActualWidth);
            if (width == 0) return;
            var columns = width >= 600 ? 2 : 1;
            foreach (FrameworkElement card in devicesPanel.Children) card.Width = Math.Max(0, width / columns - 12);
        }
        devicesPanel.SizeChanged += (_, _) => ResizeCards();
        refresh.Add(() =>
        {
            var devices = snapshot.Hardware.OrderBy(h => Array.IndexOf(categoryOrder, h.Category)).ToList();
            var key = string.Join("|", devices.Select(h => h.Id + h.Name + string.Join(",", h.Sensors.Where(s => s.Kind == "Temperature").Select(s => s.Id + s.Name))));
            if (inventoryKey != key)
            {
                inventoryKey = key;
                renders.Clear();
                devicesPanel.Children.Clear();
                foreach (var device in devices)
                {
                    var sensors = OrderedTemperatures(device).ToList();
                    var name = Text(device.Name, 14, true);
                    var categoryLabel = Text(CategoryName(device.Category), 11, true, AccentColor);
                    var body = Stack(categoryLabel, name);
                    name.Margin = new Thickness(0, 4, 0, 12);
                    var primaryRows = Enumerable.Range(0, Math.Min(3, sensors.Count)).Select(_ => CreateTemperatureRow()).ToList();
                    foreach (var row in primaryRows) body.Children.Add(row.Root);
                    var unavailable = Hint("设备未提供温度传感器。");
                    unavailable.Visibility = sensors.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    body.Children.Add(unavailable);
                    var allRows = new Dictionary<string, TemperatureRow>();
                    if (sensors.Count > 3)
                    {
                        var all = new StackPanel();
                        foreach (var sensor in sensors)
                        {
                            var row = CreateTemperatureRow();
                            allRows.Add(sensor.Id, row);
                            all.Children.Add(row.Root);
                        }
                        body.Children.Add(new Expander { Header = $"全部温度传感器 · {sensors.Count} 项", Content = all, Margin = new Thickness(0, 7, 0, 0) });
                    }
                    var targetId = device.Id;
                    var detail = Button("查看温度曲线 →", () => { OpenTemperatureDetail(targetId); return Task.CompletedTask; });
                    detail.HorizontalAlignment = HorizontalAlignment.Left;
                    detail.Margin = new Thickness(0, 12, 0, 0);
                    detail.Padding = new Thickness(11, 7, 11, 7);
                    body.Children.Add(detail);
                    var card = Card(body, new Thickness(0, 0, 12, 12));
                    card.SetResourceReference(Border.BackgroundProperty, "Elevated");
                    card.Padding = new Thickness(15);
                    devicesPanel.Children.Add(card);
                    renders.Add(device.Id, current =>
                    {
                        var ordered = OrderedTemperatures(current).ToList();
                        for (var i = 0; i < primaryRows.Count; i++) UpdateTemperatureRow(primaryRows[i], ordered[i], current);
                        foreach (var sensor in ordered) if (allRows.TryGetValue(sensor.Id, out var row)) UpdateTemperatureRow(row, sensor, current);
                        unavailable.Text = "温度 — · " + TemperatureReason(null, current);
                        unavailable.ToolTip = TemperatureTooltip(null, current);
                        detail.IsEnabled = ordered.Count > 0;
                    });
                }
                ResizeCards();
            }
            foreach (var device in devices) renders[device.Id](device);
            var availableDevices = devices.Count(h => h.Sensors.Any(s => s.Kind == "Temperature" && s.Value.HasValue));
            counts.Text = $"当前 {availableDevices} 个设备提供了温度读数。每项显示传感器名称；多传感器可展开查看。";
            permission.Text = snapshot.IsAdministrator
                ? "当前已是管理员权限。没有读数的设备仍可能没有公开温度传感器或驱动接口；请悬停查看具体原因。"
                : "当前为标准权限。管理员权限可能补充 CPU、主板、存储温度；是否可读取仍取决于设备与驱动。";
            elevation.Visibility = snapshot.IsAdministrator ? Visibility.Collapsed : Visibility.Visible;
        });
        return section;
    }

    private sealed record TemperatureRow(Grid Root, TextBlock Name, TextBlock Value, TextBlock Reason);

    private static TemperatureRow CreateTemperatureRow()
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var name = Text("", 12);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.Margin = new Thickness(0, 0, 12, 0);
        var value = Text("—", 18, true, AccentColor);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(value, 1);
        var reason = Hint("");
        Grid.SetRow(reason, 1);
        Grid.SetColumnSpan(reason, 2);
        grid.Children.Add(name);
        grid.Children.Add(value);
        grid.Children.Add(reason);
        return new(grid, name, value, reason);
    }

    private static void UpdateTemperatureRow(TemperatureRow row, SensorReading sensor, HardwareDevice device)
    {
        row.Name.Text = sensor.Name;
        row.Value.Text = Reading(sensor);
        row.Reason.Text = TemperatureReason(sensor, device);
        row.Reason.Visibility = sensor.Value.HasValue ? Visibility.Collapsed : Visibility.Visible;
        row.Root.ToolTip = TemperatureTooltip(sensor, device);
    }

    private static IEnumerable<SensorReading> OrderedTemperatures(HardwareDevice device) => device.Sensors.Where(s => s.Kind == "Temperature")
        .OrderByDescending(s => s.Value.HasValue)
        .ThenBy(s => s.Name.Contains("Tctl/Tdie", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("CPU Package", StringComparison.OrdinalIgnoreCase)
            || s.Name.Equals("Package", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("GPU 温度", StringComparison.OrdinalIgnoreCase)
            || s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Composite", StringComparison.OrdinalIgnoreCase) ? 0 : 1);

    private static string TemperatureReason(SensorReading? sensor, HardwareDevice? device)
    {
        if (device?.Id == "windows/npu-unavailable") return "未检测到公开的 NPU 设备。";
        if (sensor is null) return "设备未提供温度传感器。";
        return sensor.UnavailableReason?.Split(['。', '；', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "没有可用的温度读数。";
    }

    private static string TemperatureTooltip(SensorReading? sensor, HardwareDevice? device) => sensor is null
        ? $"{device?.Name ?? "未检测到设备"}\n{TemperatureReason(sensor, device)}"
        : $"{sensor.Name}\n当前温度：{Reading(sensor)}\n数据来源：{sensor.Source}\n{sensor.UnavailableReason}";

    private void OpenTemperatureDetail(string targetId)
    {
        var device = snapshot.Hardware.FirstOrDefault(h => h.Id == targetId);
        if (device is null) return;
        category = device.Category;
        deviceId = device.Id;
        chartSensorId = OrderedTemperatures(device).FirstOrDefault()?.Id;
        focusTemperatureChart = true;
        Navigate(1);
    }
}
