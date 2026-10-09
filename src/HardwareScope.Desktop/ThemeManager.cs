using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace HardwareScope.Desktop;

public enum ThemePreference { System, Light, Dark }

public sealed class ThemeManager : IDisposable
{
    private const string PreferenceKey = @"Software\HardwareScope\Appearance";
    private readonly string registryPath;
    internal string RegistryPath => registryPath;
    private readonly bool isolated;
    private readonly uint appearanceMessage = RegisterWindowMessage("HardwareScope.AppearanceChanged.v1");
    private HwndSource? source;
    public ThemePreference Preference { get; private set; }
    public bool IsDark { get; private set; }
    public event Action? Changed;

    public ThemeManager(bool isolated = false, ThemePreference? preview = null)
    {
        this.isolated = isolated;
        registryPath = isolated ? PreferenceKey + "Smoke\\" + Guid.NewGuid().ToString("N") : PreferenceKey;
        Preference = preview ?? ReadPreference();
        Apply();
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
    }

    public void Attach(Window window)
    {
        source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        source.AddHook(WindowMessage);
        ChangeWindowMessageFilterEx(source.Handle, appearanceMessage, 1, IntPtr.Zero);
        SetTitleBar();
    }

    public void Select(ThemePreference preference)
    {
        using var key = Registry.CurrentUser.CreateSubKey(registryPath);
        var previous = key.GetValue("Theme");
        if (previous is not null) key.SetValue("ThemeBackup", previous, key.GetValueKind("Theme"));
        key.SetValue("Theme", (int)preference, RegistryValueKind.DWord);
        Preference = preference;
        Apply();
        if (!isolated) SendNotifyMessage(new IntPtr(0xffff), appearanceMessage, IntPtr.Zero, IntPtr.Zero);
    }

    internal ThemePreference ReadPreference()
    {
        using var key = Registry.CurrentUser.OpenSubKey(registryPath);
        return key?.GetValue("Theme") is int value && value is >= 0 and <= 2 ? (ThemePreference)value : ThemePreference.System;
    }

    internal static bool ResolveDark(ThemePreference preference, bool systemLight) => preference == ThemePreference.Dark || preference == ThemePreference.System && !systemLight;

    private void Apply()
    {
        using var system = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        IsDark = ResolveDark(Preference, system?.GetValue("AppsUseLightTheme") is not int value || value != 0);
        // Native controls and WPF share the KUNLUN void / snowline semantic palette.
        string[] names = ["Canvas", "Panel", "Surface", "Input", "Elevated", "Text", "Muted", "Stroke", "StrongStroke", "Accent", "AccentFill", "OnAccent", "Selection", "Hover", "Warning", "Danger", "Success", "Chart1", "Chart2", "Chart3", "Chart4"];
        string[] dark = ["#06080D", "#04060A", "#0D1320", "#02040A", "#131A2A", "#E3EAF3", "#95A3B4", "#1A2540", "#2A3A5C", "#5FD9FF", "#00B8FF", "#04060A", "#102C3E", "#131C2E", "#FFB000", "#FF7878", "#5CFFB8", "#5FD9FF", "#6C95FF", "#5CFFB8", "#C2CDDA"];
        string[] light = ["#E2E9F2", "#D2DCE8", "#F7FAFD", "#D9E2EE", "#FFFFFF", "#0C1526", "#4A5C7A", "#C7D3E3", "#9DAEC7", "#0075A8", "#0075A8", "#FFFFFF", "#D6EAF5", "#E4ECF6", "#956100", "#B22020", "#00804C", "#0075A8", "#2A5FD6", "#00804C", "#4A5C7A"];
        var palette = IsDark ? dark : light;
        for (var i = 0; i < names.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette[i]));
            brush.Freeze();
            Application.Current.Resources[names[i]] = brush;
        }
        SetTitleBar();
        Changed?.Invoke();
    }

    private void SetTitleBar()
    {
        if (source is null) return;
        var dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(source.Handle, 20, ref dark, sizeof(int));
    }

    private void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        if (Preference == ThemePreference.System && !Application.Current.Dispatcher.HasShutdownStarted)
            Application.Current.Dispatcher.BeginInvoke(Apply);
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == appearanceMessage && !isolated) { Preference = ReadPreference(); Apply(); }
        else if (message == 0x001a && Preference == ThemePreference.System) Apply();
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        source?.RemoveHook(WindowMessage);
        if (isolated) Registry.CurrentUser.DeleteSubKey(registryPath, false);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool SendNotifyMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeFilter);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
