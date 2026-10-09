using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using HardwareScope.Core;

namespace HardwareScope.Desktop;

public sealed partial class MainWindow
{
    private sealed class ProcessRow(ProcessSample sample) : INotifyPropertyChanged
    {
        private ProcessSample sample = sample;
        public int Pid => sample.Pid;
        public string InstanceId => sample.InstanceId;
        public string Name => sample.Name;
        public string? Path => sample.Path;
        public double? CpuPercent => sample.CpuPercent;
        public double MemoryMb => sample.MemoryMb;
        public double? GpuPercent => sample.GpuPercent;
        public double? NpuPercent => sample.NpuPercent;
        public double? VramMb => sample.VramMb;
        public double? IoReadKb => sample.IoReadBytesPerSecond / 1024;
        public double? IoWriteKb => sample.IoWriteBytesPerSecond / 1024;
        public double? NetworkKb => sample.NetworkBytesPerSecond / 1024;
        public string Priority => PriorityLabel(sample.Priority);
        public bool CanControl => sample.CanControl;
        public string? ControlReason => sample.ControlReason;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Update(ProcessSample value) { sample = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("")); }
    }

    private sealed record PriorityChoice(string Id, string Name) { public override string ToString() => Name; }
    private static string PriorityLabel(string priority) => priority switch { "Idle" => "低", "BelowNormal" => "低于正常", "Normal" => "正常", "AboveNormal" => "高于正常", "High" => "高", "RealTime" => "实时", _ => priority };

    private void BuildProcesses()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        var search = new TextBox { Width = 245, Margin = new Thickness(0, 0, 10, 8), ToolTip = "按进程名、路径或 PID 搜索" };
        search.SetCurrentValue(TextBox.TextProperty, "");
        var searchBox = Stack(Hint("搜索名称 / PID"), search);
        toolbar.Children.Add(searchBox);
        var picker = new ComboBox { Width = 141, Margin = new Thickness(0, 0, 10, 8), ItemsSource = new[] { "Idle", "BelowNormal", "Normal", "AboveNormal", "High" }.Select(p => new PriorityChoice(p, PriorityLabel(p))).ToList(), SelectedIndex = 2 };
        toolbar.Children.Add(Stack(Hint("性能优先级"), picker));
        var rows = new ObservableCollection<ProcessRow>();
        var grid = new DataGrid { ItemsSource = rows };
        grid.Columns.Add(Column("应用 / 进程", nameof(ProcessRow.Name), 190));
        grid.Columns.Add(Column("PID", nameof(ProcessRow.Pid), 68));
        var cpu = Column("CPU %", nameof(ProcessRow.CpuPercent), 90, "{0:0.0}");
        cpu.SortDirection = ListSortDirection.Descending;
        grid.Columns.Add(cpu);
        grid.Columns.Add(Column("内存 MB", nameof(ProcessRow.MemoryMb), 112, "{0:0.0}"));
        grid.Columns.Add(Column("GPU %", nameof(ProcessRow.GpuPercent), 95, "{0:0.0}"));
        grid.Columns.Add(Column("NPU %", nameof(ProcessRow.NpuPercent), 90, "{0:0.0}"));
        grid.Columns.Add(Column("显存 MB", nameof(ProcessRow.VramMb), 107, "{0:0.0}"));
        grid.Columns.Add(Column("I/O 读取 KB/s", nameof(ProcessRow.IoReadKb), 142, "{0:0.0}"));
        grid.Columns.Add(Column("I/O 写入 KB/s", nameof(ProcessRow.IoWriteKb), 142, "{0:0.0}"));
        grid.Columns.Add(Column("网络 KB/s", nameof(ProcessRow.NetworkKb), 123, "{0:0.0}"));
        grid.Columns.Add(Column("优先级", nameof(ProcessRow.Priority), 112));
        var selected = Hint("选择一个进程以查看路径和可用操作。");
        var feedback = Text("", 12, false, AccentColor);
        var priorityButton = Button("设置优先级", async () =>
        {
            if (grid.SelectedItem is not ProcessRow row || picker.SelectedItem is not PriorityChoice priority || !row.CanControl) return;
            var result = await host.SetPriorityAsync(row.Pid, row.InstanceId, priority.Id);
            feedback.Text = result.Message;
            feedback.SetResourceReference(TextBlock.ForegroundProperty, result.Success ? AccentColor : "Warning");
        });
        var endButton = Button("结束进程", async () =>
        {
            if (grid.SelectedItem is not ProcessRow row || !row.CanControl) return;
            if (MessageBox.Show(this, $"确定强制结束 {row.Name}（PID {row.Pid}）？\n\n此操作可能丢失该应用尚未保存的数据。", "结束进程", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var result = await host.EndProcessAsync(row.Pid, row.InstanceId);
            feedback.Text = result.Message;
            feedback.SetResourceReference(TextBlock.ForegroundProperty, result.Success ? AccentColor : "Warning");
        });
        priorityButton.Margin = new Thickness(0, 21, 10, 8);
        endButton.Margin = new Thickness(0, 21, 0, 8);
        endButton.SetResourceReference(StyleProperty, "DangerButton");
        toolbar.Children.Add(priorityButton);
        toolbar.Children.Add(endButton);
        root.Children.Add(toolbar);
        var table = Card(grid, new Thickness(0));
        table.Padding = new Thickness(0);
        Grid.SetRow(table, 1);
        root.Children.Add(table);
        var count = Hint("");
        var foot = Stack(count, selected, feedback, Hint("CPU 按整机逻辑处理器归一化。I/O 包含文件和设备等读写，并非纯磁盘。— 表示指标不可用；受保护进程不可控制。"));
        foot.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(foot, 2);
        root.Children.Add(foot);
        content.Children.Add(root);
        var view = CollectionViewSource.GetDefaultView(rows);
        view.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.CpuPercent), ListSortDirection.Descending));
        view.Filter = value => value is ProcessRow row && (string.IsNullOrWhiteSpace(search.Text) || row.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase) || row.Pid.ToString().Contains(search.Text) || (row.Path?.Contains(search.Text, StringComparison.OrdinalIgnoreCase) ?? false));
        search.TextChanged += (_, _) => view.Refresh();
        Action selection = () =>
        {
            var row = grid.SelectedItem as ProcessRow;
            priorityButton.IsEnabled = endButton.IsEnabled = row?.CanControl == true;
            selected.Text = row is null ? "选择一个进程以查看路径和可用操作。" : $"{row.Name}（{row.Pid}） · {row.Path ?? "路径不可访问"}{(row.CanControl ? "" : "\n" + row.ControlReason)}";
        };
        grid.SelectionChanged += (_, _) => selection();
        refresh.Add(() =>
        {
            var selectedInstance = (grid.SelectedItem as ProcessRow)?.InstanceId;
            var samples = snapshot.Processes.ToDictionary(p => p.InstanceId);
            for (var i = rows.Count - 1; i >= 0; i--)
            {
                if (samples.Remove(rows[i].InstanceId, out var sample)) rows[i].Update(sample);
                else rows.RemoveAt(i);
            }
            foreach (var sample in samples.Values) rows.Add(new ProcessRow(sample));
            view.Refresh();
            if (selectedInstance is not null) grid.SelectedItem = rows.FirstOrDefault(r => r.InstanceId == selectedInstance);
            count.Text = $"共 {snapshot.Processes.Count} 个进程 · 可按 CPU、GPU、NPU、内存、显存、I/O 与网络占用排序";
            selection();
        });
        selection();
    }
}
