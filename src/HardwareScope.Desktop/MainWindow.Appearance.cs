using System.Windows;
using System.Windows.Controls;

namespace HardwareScope.Desktop;

public sealed partial class MainWindow
{
    private sealed record ThemeChoice(ThemePreference Value, string Name) { public override string ToString() => Name; }
    private ComboBox ThemePicker()
    {
        var picker = new ComboBox
        {
            ItemsSource = new[] { new ThemeChoice(ThemePreference.System, "跟随系统"), new ThemeChoice(ThemePreference.Light, "浅色模式"), new ThemeChoice(ThemePreference.Dark, "深色模式") },
            SelectedValuePath = "Value", SelectedValue = themes.Preference,
            Margin = new Thickness(0, 6, 0, 0), MinWidth = 160,
            ToolTip = "选择后立即保存；监控与文件占用模块共享此设置。"
        };
        themePickers.Add(picker);
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is not ThemeChoice choice || choice.Value == themes.Preference) return;
            try { themes.Select(choice.Value); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException or System.Security.SecurityException)
            {
                picker.SelectedValue = themes.Preference;
                MessageBox.Show(this, "主题设置未保存：" + ex.Message, "外观设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        return picker;
    }

    private void OnThemeChanged()
    {
        foreach (var picker in themePickers) picker.SelectedValue = themes.Preference;
        themeState.Text = $"// {(themes.Preference == ThemePreference.System ? "SYSTEM / " : "")}{(themes.IsDark ? "DARK" : "LIGHT")}";
        RefreshPage();
    }
}
