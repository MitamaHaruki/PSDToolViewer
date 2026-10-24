using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using PSDToolViewer.Interop;

namespace PSDToolViewer.Services;

/// <summary>
/// ライト／ダークの切替。標準コントロールは WPF の Fluent テーマ（ThemeMode）に任せ、
/// 独自の部品が使う色（App.* のリソース）はここで設定する。
/// </summary>
public static class ThemeService
{
    private static ThemeSetting _setting = ThemeSetting.System;

    public static bool IsDark { get; private set; }

    /// <summary>Mica 背景が使えるか（Windows 11 22H2 以降）。</summary>
    public static bool IsMicaSupported => Environment.OSVersion.Version.Build >= 22621;

    public static event EventHandler? ThemeChanged;

    public static void Initialize(ThemeSetting setting)
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(_setting));
        };
        Apply(setting);
    }

    public static void Apply(ThemeSetting setting)
    {
        _setting = setting;
        var app = Application.Current;
        if (app is null) return;

        IsDark = setting switch
        {
            ThemeSetting.Dark => true,
            ThemeSetting.Light => false,
            _ => IsSystemDark(),
        };

        app.ThemeMode = setting switch
        {
            ThemeSetting.Dark => ThemeMode.Dark,
            ThemeSetting.Light => ThemeMode.Light,
            _ => ThemeMode.System,
        };

        var accent = GetAccentColor();
        var r = app.Resources;
        void Set(string key, Color color) => r[key] = Freeze(new SolidColorBrush(color));

        if (IsDark)
        {
            Set("App.WindowBackground", Color.FromRgb(0x20, 0x20, 0x20));
            Set("App.PanelBackground", Color.FromArgb(0x60, 0x1C, 0x1C, 0x1C));
            Set("App.CardBackground", Color.FromRgb(0x2C, 0x2C, 0x2C));
            Set("App.Text", Colors.White);
            Set("App.TextSecondary", Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF));
            Set("App.TextTertiary", Color.FromArgb(0x8B, 0xFF, 0xFF, 0xFF));
            Set("App.Divider", Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
            Set("App.SubtleHover", Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
            Set("App.SubtlePressed", Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF));
            Set("App.ControlStroke", Color.FromArgb(0x8B, 0xFF, 0xFF, 0xFF));
            Set("App.ControlFill", Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
            Set("App.Selection", WithAlpha(Lighten(accent, 0.25), 0x45));
            Set("App.OverlayBackground", Color.FromArgb(0x80, 0x00, 0x00, 0x00));
            Set("App.InfoBackground", Color.FromRgb(0x24, 0x31, 0x40));
            Set("App.Accent", Lighten(accent, 0.35));
            Set("App.AccentText", Colors.Black);
        }
        else
        {
            Set("App.WindowBackground", Color.FromRgb(0xF3, 0xF3, 0xF3));
            Set("App.PanelBackground", Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
            Set("App.CardBackground", Colors.White);
            Set("App.Text", Color.FromArgb(0xE4, 0x00, 0x00, 0x00));
            Set("App.TextSecondary", Color.FromArgb(0x9E, 0x00, 0x00, 0x00));
            Set("App.TextTertiary", Color.FromArgb(0x72, 0x00, 0x00, 0x00));
            Set("App.Divider", Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
            Set("App.SubtleHover", Color.FromArgb(0x0C, 0x00, 0x00, 0x00));
            Set("App.SubtlePressed", Color.FromArgb(0x07, 0x00, 0x00, 0x00));
            Set("App.ControlStroke", Color.FromArgb(0x9E, 0x00, 0x00, 0x00));
            Set("App.ControlFill", Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
            Set("App.Selection", WithAlpha(accent, 0x30));
            Set("App.OverlayBackground", Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
            Set("App.InfoBackground", Color.FromRgb(0xE6, 0xF1, 0xFB));
            Set("App.Accent", Darken(accent, 0.1));
            Set("App.AccentText", Colors.White);
        }
        Set("App.CloseHover", Color.FromRgb(0xC4, 0x2B, 0x1C));
        r["App.CheckerBrush"] = CreateChecker(IsDark);
        // サムネイルは線画が見えるよう、テーマによらず明るい市松模様にする
        r["App.ThumbnailCheckerBrush"] = CreateChecker(dark: false, cell: 4);

        foreach (Window window in app.Windows)
            ApplyToWindow(window);

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>ウィンドウの枠（DWM）をテーマに合わせる。</summary>
    public static void ApplyToWindow(Window window, bool mica = false)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        NativeMethods.SetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, IsDark ? 1 : 0);
        if (mica && IsMicaSupported)
            NativeMethods.SetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, NativeMethods.DWMSBT_MAINWINDOW);
    }

    public static Brush CreateChecker(bool dark, double cell = 8)
    {
        var a = dark ? Color.FromRgb(0x3A, 0x3A, 0x3A) : Colors.White;
        var b = dark ? Color.FromRgb(0x2C, 0x2C, 0x2C) : Color.FromRgb(0xE3, 0xE3, 0xE3);
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(a), null, new RectangleGeometry(new Rect(0, 0, cell * 2, cell * 2))));
        var g = new GeometryGroup();
        g.Children.Add(new RectangleGeometry(new Rect(0, 0, cell, cell)));
        g.Children.Add(new RectangleGeometry(new Rect(cell, cell, cell, cell)));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(b), null, g));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, cell * 2, cell * 2),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        RenderOptions.SetEdgeMode(brush, EdgeMode.Aliased);
        brush.Freeze();
        return brush;
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static Color GetAccentColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
                return Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }
        return Color.FromRgb(0x00, 0x67, 0xC0);
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color Lighten(Color c, double k)
        => Color.FromRgb((byte)(c.R + (255 - c.R) * k), (byte)(c.G + (255 - c.G) * k), (byte)(c.B + (255 - c.B) * k));

    private static Color Darken(Color c, double k)
        => Color.FromRgb((byte)(c.R * (1 - k)), (byte)(c.G * (1 - k)), (byte)(c.B * (1 - k)));

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
