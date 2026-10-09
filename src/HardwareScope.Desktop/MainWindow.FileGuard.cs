using Microsoft.Win32;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace HardwareScope.Desktop;

public sealed partial class MainWindow
{
    private static string? FileGuardExecutable()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\HardwareScope.FileGuard.exe");
        if (key?.GetValue("") is string installed && File.Exists(installed)) return installed;
        var portable = Path.Combine(AppContext.BaseDirectory, "FileGuard", "HardwareScope.FileGuard.exe");
        return File.Exists(portable) ? portable : null;
    }

    private void BuildFileGuard()
    {
        var panel = new StackPanel();
        var path = new TextBox { Margin = new Thickness(0, 10, 0, 14), MinHeight = 34 };
        var feedback = Hint("");
        var actions = new WrapPanel();
        Task OpenModule(string? selectedPath)
        {
            var executable = FileGuardExecutable();
            if (executable is null) { feedback.Text = "请运行安装目录 FileGuard 下的独立安装程序安装文件占用模块。"; return Task.CompletedTask; }
            try
            {
                var launch = new ProcessStartInfo(executable) { UseShellExecute = true };
                if (!string.IsNullOrWhiteSpace(selectedPath)) { launch.ArgumentList.Add("/inspect"); launch.ArgumentList.Add(selectedPath); }
                Process.Start(launch);
                feedback.Text = "已打开文件占用模块。勾选占用项后可释放句柄或结束进程。";
            }
            catch (System.ComponentModel.Win32Exception ex) { feedback.Text = "无法打开文件占用模块：" + ex.Message; }
            return Task.CompletedTask;
        }
        var inspect = Button("查看路径占用", () => OpenModule(path.Text.Trim().Trim('"')));
        inspect.SetResourceReference(StyleProperty, "PrimaryButton");
        inspect.Margin = new Thickness(0, 0, 10, 8);
        var choose = Button("选择文件并查看", () =>
        {
            var dialog = new OpenFileDialog { Title = "选择要查看占用的文件", CheckFileExists = true };
            if (dialog.ShowDialog(this) == true) { path.Text = dialog.FileName; return OpenModule(dialog.FileName); }
            return Task.CompletedTask;
        });
        choose.Margin = new Thickness(0, 0, 10, 8);
        var folder = Button("选择文件夹并查看", () =>
        {
            var dialog = new OpenFolderDialog { Title = "选择要查看占用的文件夹" };
            if (dialog.ShowDialog(this) == true) { path.Text = dialog.FolderName; return OpenModule(dialog.FolderName); }
            return Task.CompletedTask;
        });
        folder.Margin = new Thickness(0, 0, 0, 8);
        actions.Children.Add(inspect); actions.Children.Add(choose); actions.Children.Add(folder);
        panel.Children.Add(Card(Stack(Text("查找文件占用", 16, true), Hint("文件无法删除、移动或重命名时，输入路径或选择文件。文件夹查询会列出其中仍被应用打开的文件。"), path, actions, feedback)));
        var settings = Button("打开模块与后台服务设置", () => OpenModule(null));
        settings.HorizontalAlignment = HorizontalAlignment.Left;
        var install = Button("安装或修复右键菜单与后台服务", () =>
        {
            var setup = Path.Combine(AppContext.BaseDirectory, "FileGuard", "HardwareScope-FileGuard-1.1.0-windows-x64-setup.exe");
            if (!File.Exists(setup)) { feedback.Text = "未找到模块安装程序，请运行 FileGuard 独立安装包。"; return Task.CompletedTask; }
            try { Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception ex) { feedback.Text = "安装程序未启动：" + ex.Message; }
            return Task.CompletedTask;
        });
        install.HorizontalAlignment = HorizontalAlignment.Left;
        install.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(Card(Stack(Text("右键菜单与安静后台服务", 16, true), Hint("安装模块后，在文件或文件夹的右键菜单选择“查看文件占用”。Windows 11 可在“显示更多选项”中找到。服务可设置开机自动启动；空闲时不扫描、不联网、不弹窗口或终端。"), settings, install)));
        panel.Children.Add(Card(Stack(Text("选择处理方式", 16, true), Hint("释放所选句柄：只关闭勾选的文件句柄，应用进程继续运行，但应用可能发生错误或丢失未保存数据。\n结束所选进程：关闭该进程持有的所有文件；未保存的数据会丢失。\n文件映射、受保护进程和内核驱动占用不能保证直接解除；模块会显示操作结果。标准权限仅操作自己的进程，其他用户或管理员应用需要在模块中请求管理员权限。"))));
        content.Children.Add(Scroll(panel));
    }
}
