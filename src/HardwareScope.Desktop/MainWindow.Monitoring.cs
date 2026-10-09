using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using HardwareScope.Core;

namespace HardwareScope.Desktop;

public sealed partial class MainWindow
{
    private void BuildOverview()
    {
        var panel = new StackPanel();
        var overviewScroll = Scroll(panel);
        var metrics = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        for (var i = 0; i < 4; i++) metrics.ColumnDefinitions.Add(new ColumnDefinition());
        string[] cats = ["CPU", "GPU", "NPU", "Memory"];
        for (var i = 0; i < cats.Length; i++)
        {
            var cat = cats[i];
            var label = Text("// " + CategoryName(cat).ToUpperInvariant(), 11, true, MutedColor);
            var value = Text("—", 34, true);
            value.Margin = new Thickness(0, 8, 0, 3);
            var detail = Hint("等待硬件采样");
            detail.MaxHeight = 40;
            detail.TextTrimming = TextTrimming.CharacterEllipsis;
            var card = Card(Stack(label, value, detail), new Thickness(i == 0 ? 0 : 7, 0, i == 3 ? 0 : 7, 14));
            Grid.SetColumn(card, i);
            metrics.Children.Add(card);
            refresh.Add(() =>
            {
                var sensor = PrimarySensor(cat);
                value.Text = Reading(sensor);
                var temperature = snapshot.Hardware.Where(h => h.Category == cat).SelectMany(OrderedTemperatures).FirstOrDefault(s => s.Value.HasValue) ?? PrimarySensor(cat, "Temperature");
                var hw = snapshot.Hardware.FirstOrDefault(h => h.Category == cat);
                detail.Text = temperature?.Value is not null ? $"温度 {Reading(temperature)} · {temperature.Name}" : "温度 — · " + TemperatureReason(temperature, hw);
                detail.ToolTip = TemperatureTooltip(temperature, hw);
            });
        }
        panel.Children.Add(metrics);
        var chart = new TrendChart { Height = 250, Margin = new Thickness(0, 13, 0, 0) };
        panel.Children.Add(Card(Stack(Text("实时负载趋势", 16, true), Hint("CPU、GPU、NPU、内存 · 最近 120 次采样"), chart)));
        refresh.Add(() => chart.SetData(cats.Select((cat, index) => new ChartSeries(CategoryName(cat), ChartColors[index], Points(PrimarySensor(cat)?.Id))).ToArray()));
        panel.Children.Add(BuildTemperatureOverview());
        var hardwareList = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var capability = Hint("等待采样…");
        panel.Children.Add(Card(Stack(Text("设备一览", 16, true), hardwareList, capability)));
        string? lastInventory = null;
        refresh.Add(() =>
        {
            var inventory = string.Join("|", snapshot.Hardware.Select(h => h.Id + h.Name));
            if (lastInventory != inventory)
            {
                lastInventory = inventory;
                hardwareList.Children.Clear();
                foreach (var cat in Categories.Keys)
                {
                    var devices = snapshot.Hardware.Where(h => h.Category == cat).ToList();
                    var targetCategory = cat;
                    var label = cat == "NPU" && devices.All(d => d.Id == "windows/npu-unavailable") ? "NPU · 未检测到" : CategoryName(cat);
                    var button = Button(label, () => { category = targetCategory; deviceId = null; Navigate(1); return Task.CompletedTask; });
                    button.Margin = new Thickness(0, 0, 8, 10);
                    button.ToolTip = devices.Count == 0 ? "未检测到可监控设备" : string.Join("\n", devices.Select(d => d.Name));
                    hardwareList.Children.Add(button);
                }
            }
            capability.Text = "横线表示当前设备、驱动或权限未提供该指标；不会用零值代替缺失的测量。";
        });
        var notices = Hint("");
        panel.Children.Add(Card(Stack(Text("采样状态与能力说明", 16, true), notices)));
        refresh.Add(() => notices.Text = snapshot.Notices.Count > 0 ? string.Join("\n", snapshot.Notices.Select(n => "• " + n)) : "采样正常；详细的可用性与来源请在硬件详情中查看。");
        content.Children.Add(overviewScroll);
    }

    private sealed record CategoryChoice(string Id, string Name) { public override string ToString() => Name; }
    private sealed record DeviceChoice(string Id, string Name) { public override string ToString() => Name; }
    private sealed record SensorChoice(string Id, string Name) { public override string ToString() => Name; }
    private sealed record SensorRow(string Name, string Kind, string Value, string Source, string Availability);

    private void BuildHardware()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        var categoryPicker = new ComboBox { Width = 230, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16), ItemsSource = Categories.Select(p => new CategoryChoice(p.Key, p.Value)).ToList(), SelectedValuePath = "Id", SelectedValue = category };
        categoryPicker.SelectionChanged += (_, _) =>
        {
            if (categoryPicker.SelectedItem is CategoryChoice c && c.Id != category) { category = c.Id; deviceId = null; chartSensorId = null; Navigate(1); }
        };
        root.Children.Add(categoryPicker);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        var deviceList = new ListBox { Margin = new Thickness(0, 0, 14, 0), SelectedValuePath = "Id" };
        var deviceText = new FrameworkElementFactory(typeof(TextBlock));
        deviceText.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        deviceText.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        deviceList.ItemTemplate = new DataTemplate { VisualTree = deviceText };
        ScrollViewer.SetHorizontalScrollBarVisibility(deviceList, ScrollBarVisibility.Disabled);
        body.Children.Add(deviceList);
        var details = new StackPanel();
        var deviceTitle = Text("正在读取设备…", 18, true);
        var deviceStatus = Hint("");
        deviceStatus.Margin = new Thickness(0, 7, 0, 0);
        var oc = Hint("");
        oc.Margin = new Thickness(0, 9, 0, 0);
        details.Children.Add(Card(Stack(deviceTitle, deviceStatus, oc)));
        var sensors = new DataGrid { MinHeight = 90, MaxHeight = 365, CanUserSortColumns = true };
        sensors.Columns.Add(Column("指标", nameof(SensorRow.Name), 165));
        sensors.Columns.Add(Column("类型", nameof(SensorRow.Kind), 86));
        sensors.Columns.Add(Column("当前值", nameof(SensorRow.Value), 120));
        sensors.Columns.Add(Column("数据来源", nameof(SensorRow.Source), 200));
        sensors.Columns.Add(Column("可用性", nameof(SensorRow.Availability), 290));
        details.Children.Add(Card(Stack(Text("传感器读数", 16, true), Hint("支持指标由硬件、驱动和访问权限决定。可横向滚动查看来源。"), sensors)));
        var picker = new ComboBox { Margin = new Thickness(0, 12, 0, 0), SelectedValuePath = "Id" };
        var trend = new TrendChart { Height = 215, Margin = new Thickness(0, 11, 0, 0) };
        var trendPanel = Stack(Text("指标曲线", 16, true), picker, trend);
        details.Children.Add(Card(trendPanel));
        var detailsScroll = Scroll(details);
        if (focusTemperatureChart)
        {
            focusTemperatureChart = false;
            detailsScroll.Loaded += (_, _) => detailsScroll.ScrollToVerticalOffset(trendPanel.TranslatePoint(new Point(), details).Y);
        }
        Grid.SetColumn(detailsScroll, 1);
        body.Children.Add(detailsScroll);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        content.Children.Add(root);
        string? hardwareKey = null;
        string? sensorKey = null;
        Action update = () =>
        {
            var devices = snapshot.Hardware.Where(h => h.Category == category).ToList();
            var key = string.Join("|", devices.Select(h => h.Id + h.Name));
            if (hardwareKey != key)
            {
                hardwareKey = key;
                deviceList.ItemsSource = devices.Select(d => new DeviceChoice(d.Id, d.Name)).ToList();
                if (devices.All(d => d.Id != deviceId)) deviceId = devices.FirstOrDefault()?.Id;
                deviceList.SelectedValue = deviceId;
            }
            var device = devices.FirstOrDefault(d => d.Id == deviceId);
            deviceTitle.Text = device?.Name ?? $"未检测到{CategoryName(category)}";
            deviceStatus.Text = device is not null ? HardwareStatus(device.Status) : "请确认设备存在并已安装驱动；部分硬件未向 Windows 提供传感器。";
            oc.Text = device is not null ? $"超频状态：{OverclockName(device.Overclock.State)}\n{device.Overclock.Evidence}\n调优归属：{device.Overclock.Attribution}" : "";
            sensors.ItemsSource = device?.Sensors.Select(s => new SensorRow(s.Name, KindName(s.Kind), Reading(s), s.Source, s.Value.HasValue ? "可用" : s.UnavailableReason ?? "未提供测量")).ToList();
            var candidates = device?.Sensors.Where(s => s.Kind is not "Other" and not "Limit").ToList() ?? [];
            var sk = string.Join("|", candidates.Select(s => s.Id));
            if (sensorKey != sk)
            {
                sensorKey = sk;
                picker.ItemsSource = candidates.Select(s => new SensorChoice(s.Id, s.Name + " · " + KindName(s.Kind))).ToList();
                if (candidates.All(s => s.Id != chartSensorId)) chartSensorId = candidates.FirstOrDefault(s => s.Kind == "Load" && s.Value.HasValue)?.Id ?? candidates.FirstOrDefault(s => s.Value.HasValue)?.Id ?? candidates.FirstOrDefault()?.Id;
                picker.SelectedValue = chartSensorId;
            }
            var selected = candidates.FirstOrDefault(s => s.Id == chartSensorId);
            trend.SetData(selected is not null ? [new ChartSeries(selected.Name, ChartColors[0], Points(selected.Id))] : [], selected?.Unit ?? "", selected?.Unit == "%");
        };
        deviceList.SelectionChanged += (_, _) => { if (deviceList.SelectedItem is DeviceChoice d) { deviceId = d.Id; update(); } };
        picker.SelectionChanged += (_, _) => { if (picker.SelectedItem is SensorChoice s) { chartSensorId = s.Id; update(); } };
        refresh.Add(update);
    }

    private sealed record TuningRow(string Category, string Name, string State, string Offset, string Percent, string Evidence, string Attribution);

    private void BuildTuning()
    {
        var panel = new StackPanel();
        panel.Children.Add(Card(Stack(Text("测量与归属分开显示", 16, true), Hint("频率超过参考值需要处理器规格、工作状态和驱动证据共同判断。检测到调优软件并不能证明它修改了当前设备；缺少可靠证据时会显示无法确认。"))));
        var grid = new DataGrid { MinHeight = 120, MaxHeight = 290 };
        grid.Columns.Add(Column("分类", nameof(TuningRow.Category), 85));
        grid.Columns.Add(Column("硬件", nameof(TuningRow.Name), 185));
        grid.Columns.Add(Column("超频状态", nameof(TuningRow.State), 140));
        grid.Columns.Add(Column("频率偏移", nameof(TuningRow.Offset), 120));
        grid.Columns.Add(Column("幅度", nameof(TuningRow.Percent), 90));
        grid.Columns.Add(Column("实测证据", nameof(TuningRow.Evidence), 300));
        grid.Columns.Add(Column("调优归属", nameof(TuningRow.Attribution), 330));
        panel.Children.Add(Card(Stack(Text("超频证据", 16, true), grid)));
        var limits = new StackPanel { Margin = new Thickness(0, 11, 0, 0) };
        panel.Children.Add(Card(Stack(Text("性能限制与节流", 16, true), Hint("温度、功率、电流限制等以驱动提供的标志为准。"), limits)));
        var tools = new StackPanel { Margin = new Thickness(0, 11, 0, 0) };
        panel.Children.Add(Card(Stack(Text("检测到的调优工具", 16, true), tools)));
        refresh.Add(() =>
        {
            grid.ItemsSource = snapshot.Hardware.Select(h => new TuningRow(CategoryName(h.Category), h.Name, OverclockName(h.Overclock.State), h.Overclock.OffsetMHz is double mhz ? $"{mhz:+0.#;-0.#;0} MHz" : "—", h.Overclock.Percent is double pct ? $"{pct:+0.#;-0.#;0}%" : "—", h.Overclock.Evidence, h.Overclock.Attribution)).ToList();
            limits.Children.Clear();
            var readings = snapshot.Hardware.SelectMany(h => h.Sensors.Where(s => s.Kind == "Limit").Select(s => (Hardware: h.Name, Sensor: s))).ToList();
            foreach (var reading in readings)
            {
                var s = reading.Sensor;
                var state = s.Value.HasValue ? s.Value > 0 ? "已触发" : "未触发" : "无法读取";
                limits.Children.Add(Text($"{reading.Hardware} · {s.Name}：{state}  {s.UnavailableReason}", 12, false, s.Value > 0 ? "Warning" : MutedColor));
            }
            if (readings.Count == 0) limits.Children.Add(Hint("此系统未提供可靠的性能限制标志；不根据温度猜测限制状态。"));
            tools.Children.Clear();
            foreach (var tool in snapshot.TuningTools) tools.Children.Add(Stack(Text(tool.Name, 13, true), Hint(tool.Evidence), Hint(tool.Attribution)));
            if (snapshot.TuningTools.Count == 0) tools.Children.Add(Hint("未发现可识别的调优软件证据。"));
        });
        content.Children.Add(Scroll(panel));
    }
}
