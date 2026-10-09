using Microsoft.Win32;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HardwareScope.Desktop;

internal static class ThemeVerification
{
    internal static int Run(string output)
    {
        var checks = new List<object>();
        var failed = false;
        void Check(string name, bool passed) { checks.Add(new { name, passed }); failed |= !passed; }
        using var themes = new ThemeManager(true);
        var dynamicLabel = new TextBlock();
        dynamicLabel.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var primary = new Button { Content = "SAVE" };
        primary.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        var panel = new StackPanel(); panel.Children.Add(dynamicLabel); panel.Children.Add(primary);
        var window = new Window { Content = panel, Width = 120, Height = 100, Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
        window.Show();
        window.UpdateLayout();
        themes.Select(ThemePreference.Light);
        Check("Light preference is persisted in an isolated user key", themes.ReadPreference() == ThemePreference.Light && !themes.IsDark);
        var lightText = dynamicLabel.Foreground;
        themes.Select(ThemePreference.Dark);
        window.UpdateLayout();
        Check("Dark preference is persisted", themes.ReadPreference() == ThemePreference.Dark && themes.IsDark);
        using (var key = Registry.CurrentUser.OpenSubKey(themes.RegistryPath))
            Check("Previous preference is backed up before replacement", key?.GetValue("ThemeBackup") is int previous && previous == 1);
        Check("Existing controls update without reconstruction", dynamicLabel.Foreground != lightText && ((SolidColorBrush)dynamicLabel.Foreground).Color == ((SolidColorBrush)Application.Current.Resources["Text"]).Color);
        Check("Manual light ignores a dark system", !ThemeManager.ResolveDark(ThemePreference.Light, false));
        Check("Manual dark ignores a light system", ThemeManager.ResolveDark(ThemePreference.Dark, true));
        Check("System preference resolves both Windows modes", !ThemeManager.ResolveDark(ThemePreference.System, true) && ThemeManager.ResolveDark(ThemePreference.System, false));
        themes.Select(ThemePreference.System);
        Check("System preference is retained after manual choices", themes.ReadPreference() == ThemePreference.System);
        foreach (var preference in new[] { ThemePreference.Light, ThemePreference.Dark })
        {
            themes.Select(preference);
            window.UpdateLayout();
            foreach (var key in new[] { "Text", "Muted", "Accent", "Warning", "Danger" })
                Check($"{preference} {key} contrast on panels is at least 4.5:1", Contrast(Color(key), Color("Surface")) >= 4.5);
            Check($"{preference} primary action text has sufficient contrast", Contrast(Color("OnAccent"), Color("AccentFill")) >= 4.5);
            var label = FindLabel(primary);
            Check($"{preference} rendered primary action inherits its text color", label is not null && ((SolidColorBrush)label.Foreground).Color == Color("OnAccent"));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { timestamp = DateTimeOffset.Now, checks }, new JsonSerializerOptions { WriteIndented = true }));
        window.Close();
        return failed ? 1 : 0;
    }

    private static Color Color(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;
    private static TextBlock? FindLabel(DependencyObject parent)
    {
        if (parent is TextBlock text) return text;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindLabel(VisualTreeHelper.GetChild(parent, i)) is TextBlock child) return child;
        return null;
    }
    private static double Contrast(Color a, Color b)
    {
        static double Linear(byte value) { var x = value / 255d; return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        static double Luminance(Color value) => 0.2126 * Linear(value.R) + 0.7152 * Linear(value.G) + 0.0722 * Linear(value.B);
        var first = Luminance(a); var second = Luminance(b);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }
}
