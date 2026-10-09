using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using HardwareScope.Core;

namespace HardwareScope.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly MonitorHost host;
    private readonly Func<Task> exit;
    private readonly ThemeManager themes;
    private readonly Grid content = new();
    private readonly TextBlock title = Text("性能总览", 26, true);
    private readonly TextBlock subtitle = Text("当前电脑的实时运行状态", 13, false, MutedColor);
    private readonly TextBlock status = Text("正在连接采样器…", 11, false, MutedColor);
    private readonly List<Button> navigation = [];
    private readonly List<ComboBox> themePickers = [];
    private readonly TextBlock themeState = Text("", 10, false, MutedColor);
    private readonly TextBlock pageMarker = Text("// 01   OVERVIEW", 11, true, AccentColor);
    private readonly Dictionary<string, List<ChartPoint>> history = [];
    private readonly List<Action> refresh = [];
    private MonitorSnapshot snapshot;
    private int page;
    private string category = "CPU";
    private string? deviceId;
    private string? chartSensorId;
    private bool focusTemperatureChart;
    private const string AccentColor = "Accent";
    private const string MutedColor = "Muted";
    private static Color[] ChartColors => Enumerable.Range(1, 4).Select(i => Brush("Chart" + i).Color).ToArray();
    private static readonly Dictionary<string, string> Categories = new() { ["Motherboard"] = "主板", ["CPU"] = "CPU", ["GPU"] = "GPU", ["NPU"] = "NPU", ["Memory"] = "内存", ["VRAM"] = "显存", ["Network"] = "网卡", ["Storage"] = "存储" };

    public MainWindow(MonitorHost monitorHost, Func<Task> exitAction, ThemeManager themeManager)
    {
        host = monitorHost;
        exit = exitAction;
        themes = themeManager;
        snapshot = host.Current;
        SetResourceReference(StyleProperty, typeof(Window));
        Title = "HardwareScope · 硬件与性能监控";
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/app.ico"));
        Width = 1280;
        Height = 860;
        MinWidth = 960;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new Grid();
        root.SetResourceReference(Panel.BackgroundProperty, "Canvas");
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(224) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new DockPanel { Margin = new Thickness(16, 28, 16, 20) };
        var brand = new StackPanel { Margin = new Thickness(8, 0, 0, 28) };
        var logo = new Grid { Width = 38, Height = 38, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 11) };
        var mark = new ChamferBorder { Cut = 8, BorderThickness = new Thickness(1) };
        mark.SetResourceReference(Border.BackgroundProperty, "Selection");
        mark.SetResourceReference(Border.BorderBrushProperty, "Accent");
        logo.Children.Add(mark);
        var symbol = Text("H", 24, true, AccentColor);
        symbol.HorizontalAlignment = HorizontalAlignment.Center;
        symbol.VerticalAlignment = VerticalAlignment.Center;
        logo.Children.Add(symbol);
        brand.Children.Add(logo);
        brand.Children.Add(Text("HARDWARE", 19, true));
        brand.Children.Add(Text("SCOPE / 性能监控", 11, false, MutedColor));
        DockPanel.SetDock(brand, Dock.Top);
        sidebar.Children.Add(brand);
        var sideBottom = new StackPanel { Margin = new Thickness(9, 14, 0, 0) };
        sideBottom.Children.Add(Text("●  LOCAL MONITOR", 11, false, AccentColor));
        sideBottom.Children.Add(Text("本地采样 · 安静运行", 11, false, MutedColor));
        themeState.Margin = new Thickness(0, 10, 0, 0);
        sideBottom.Children.Add(themeState);
        var quit = Button("退出监控", async () => await exit());
        quit.Margin = new Thickness(0, 16, 0, 0);
        sideBottom.Children.Add(quit);
        DockPanel.SetDock(sideBottom, Dock.Bottom);
        sidebar.Children.Add(sideBottom);
        var nav = new StackPanel();
        nav.Children.Add(Text("// WORKSPACE", 10, true, MutedColor));
        string[] labels = ["01   性能总览", "02   硬件详情", "03   应用与进程", "04   超频与限制", "05   手机连接", "06   设置", "07   文件占用"];
        for (var i = 0; i < labels.Length; i++)
        {
            var index = i;
            var item = Button(labels[i], () => { Navigate(index); return Task.CompletedTask; });
            item.HorizontalContentAlignment = HorizontalAlignment.Left;
            item.Padding = new Thickness(16, 14, 16, 14);
            item.Margin = new Thickness(0, 3, 0, 3);
            navigation.Add(item);
            nav.Children.Add(item);
        }
        sidebar.Children.Add(new ScrollViewer { Content = nav, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var sideBorder = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Child = sidebar };
        sideBorder.SetResourceReference(Border.BackgroundProperty, "Panel");
        sideBorder.SetResourceReference(Border.BorderBrushProperty, "StrongStroke");
        root.Children.Add(sideBorder);
        var main = new Grid { Margin = new Thickness(32, 28, 28, 16) };
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition());
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 24) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        var header = new StackPanel { Margin = new Thickness(0, 0, 20, 0) };
        pageMarker.Margin = new Thickness(0, 0, 0, 6);
        header.Children.Add(pageMarker);
        header.Children.Add(title);
        subtitle.Margin = new Thickness(0, 6, 0, 0);
        header.Children.Add(subtitle);
        headerGrid.Children.Add(header);
        var appearance = Stack(Text("// APPEARANCE", 10, true, MutedColor), ThemePicker());
        appearance.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(appearance, 1);
        headerGrid.Children.Add(appearance);
        main.Children.Add(headerGrid);
        Grid.SetRow(content, 1);
        main.Children.Add(content);
        status.Margin = new Thickness(0, 14, 0, 0);
        Grid.SetRow(status, 2);
        main.Children.Add(status);
        Grid.SetColumn(main, 1);
        root.Children.Add(main);
        Content = root;
        SizeChanged += (_, _) =>
        {
            foreach (var item in navigation) item.Padding = new Thickness(16, ActualHeight < 740 ? 6 : 14, 16, ActualHeight < 740 ? 6 : 14);
        };
        SourceInitialized += (_, _) => themes.Attach(this);
        themes.Changed += OnThemeChanged;
        OnThemeChanged();
        host.SnapshotUpdated += OnSnapshot;
        IsVisibleChanged += (_, _) => { if (IsVisible) RefreshPage(); };
        Navigate(0);
    }

    public void Detach() { host.SnapshotUpdated -= OnSnapshot; themes.Changed -= OnThemeChanged; }
    public void SetStartupError(string message) => status.Text = message;
    public void SetSmokePage(int index) { if (index is >= 0 and <= 6) Navigate(index); }
    public void SetSmokeScroll(double offset) => content.Children.OfType<ScrollViewer>().FirstOrDefault()?.ScrollToVerticalOffset(offset);

    private void OnSnapshot(MonitorSnapshot value)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            snapshot = value;
            foreach (var sensor in value.Hardware.SelectMany(h => h.Sensors))
            {
                if (!history.TryGetValue(sensor.Id, out var points)) history[sensor.Id] = points = [];
                points.Add(new ChartPoint(value.Timestamp, sensor.Value));
                if (points.Count > 120) points.RemoveAt(0);
            }
            if (IsVisible) RefreshPage();
        });
    }

    private void RefreshPage()
    {
        var diagnostics = snapshot.Diagnostics;
        status.Text = $"{snapshot.MachineName}  ·  {snapshot.Timestamp.LocalDateTime:HH:mm:ss} 更新  ·  采样 {diagnostics.SampleDurationMs:0} ms  ·  监控 CPU {diagnostics.MonitorCpuPercent:0.0}% / 内存 {diagnostics.MonitorMemoryMb:0.0} MB  ·  {(snapshot.IsAdministrator ? "管理员" : "标准权限")}";
        foreach (var action in refresh.ToArray()) action();
    }

    private void Navigate(int index)
    {
        page = index;
        refresh.Clear();
        content.Children.Clear();
        if (themePickers.Count > 1) themePickers.RemoveRange(1, themePickers.Count - 1);
        for (var i = 0; i < navigation.Count; i++)
        {
            navigation[i].SetResourceReference(Control.BackgroundProperty, i == index ? "Selection" : "Panel");
            navigation[i].SetResourceReference(Control.BorderBrushProperty, i == index ? "Accent" : "Panel");
            navigation[i].SetResourceReference(Control.ForegroundProperty, i == index ? AccentColor : MutedColor);
        }
        string[] titles = ["性能总览", "硬件详情", "应用与进程", "超频与性能限制", "手机连接", "设置", "文件占用"];
        string[] descriptions = ["快速掌握电脑状态；图线保留最近 120 次采样。", "按硬件分类查看频率、温度、功耗、风扇与限制信息。", "点击列标题排序；选择一个进程后设置优先级或结束进程。", "以实测指标和软件证据显示调优状态。", "在安卓端同步监控，并按需授权远程进程控制。", "配置采样、开机启动与后台运行。", "查询文件与文件夹占用，并打开独立的安静后台模块。"];
        title.Text = titles[index];
        string[] markers = ["OVERVIEW", "HARDWARE", "PROCESSES", "PERFORMANCE", "CONNECTION", "SETTINGS", "FILE GUARD"];
        pageMarker.Text = $"// {index + 1:00}   {markers[index]}";
        subtitle.Text = descriptions[index];
        switch (index) { case 0: BuildOverview(); break; case 1: BuildHardware(); break; case 2: BuildProcesses(); break; case 3: BuildTuning(); break; case 4: BuildConnection(); break; case 5: BuildSettings(); break; case 6: BuildFileGuard(); break; }
        RefreshPage();
    }

    private static SolidColorBrush Brush(string key) => (SolidColorBrush)System.Windows.Application.Current.FindResource(key);
    private static TextBlock Text(string value, double size = 13, bool bold = false, string? color = null)
    {
        var text = new TextBlock { Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.5 };
        text.SetResourceReference(TextBlock.ForegroundProperty, color ?? "Text");
        return text;
    }
    private static Button Button(string value, Func<Task> click)
    {
        var button = new Button { Content = value };
        button.Click += async (_, _) => { button.IsEnabled = false; try { await click(); } finally { button.IsEnabled = true; } };
        return button;
    }
    private static Border Card(UIElement child, Thickness? margin = null)
    {
        var card = new ChamferBorder { BorderThickness = new Thickness(1), Padding = new Thickness(20), Margin = margin ?? new Thickness(0, 0, 0, 16), Child = child };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Stroke");
        return card;
    }
    private static StackPanel Stack(params UIElement[] children) { var panel = new StackPanel(); foreach (var child in children) panel.Children.Add(child); return panel; }
    private static ScrollViewer Scroll(UIElement child) => new() { Content = child, Padding = new Thickness(0, 0, 6, 0), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private static TextBlock Hint(string value) => Text(value, 12, false, MutedColor);
    private static DataGridTextColumn Column(string header, string path, double width = 100, string? format = null)
    {
        var binding = new Binding(path) { TargetNullValue = "—", StringFormat = format };
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(path)));
        return new DataGridTextColumn { Header = header, Binding = binding, Width = new DataGridLength(width), SortMemberPath = path, ElementStyle = style };
    }
    private SensorReading? PrimarySensor(string cat, string kind = "Load") => snapshot.Hardware.Where(h => h.Category == cat).SelectMany(h => h.Sensors).FirstOrDefault(s => s.Kind == kind && s.Value.HasValue) ?? snapshot.Hardware.Where(h => h.Category == cat).SelectMany(h => h.Sensors).FirstOrDefault(s => s.Kind == kind);
    private static string Reading(SensorReading? sensor) => sensor?.Value is double value ? $"{value:0.#} {sensor.Unit}" : "—";
    private IReadOnlyList<ChartPoint> Points(string? id) => id is not null && history.TryGetValue(id, out var points) ? points : [];
    private static string KindName(string kind) => kind switch { "Load" => "占用率", "Clock" => "频率", "Temperature" => "温度", "Power" => "功耗", "Fan" => "风扇", "Data" => "容量", "Throughput" => "吞吐", "Limit" => "性能限制", _ => "其他" };
    private static string CategoryName(string value) => Categories.GetValueOrDefault(value, value);
    private static string OverclockName(string state) => state switch { "Unknown" => "无法确认", "Confirmed" or "Detected" or "Overclocked" => "检测到超频", "NoOffset" => "未检测到时钟偏移", "NotDetected" or "Stock" => "未检测到超频", "Unsupported" => "未提供证据", _ => state };
    private static string HardwareStatus(string value) => value switch { "OK" => "当前指标正常", "Partial" => "部分指标可用", "Unavailable" => "当前传感器不可用", "Disconnected" => "设备未连接", _ => value };
}
