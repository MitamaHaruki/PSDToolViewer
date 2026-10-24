using System.Text.Json;
using System.Text.Json.Serialization;

namespace PSDToolViewer.Services;

public enum ThemeSetting
{
    System,
    Light,
    Dark,
}

public enum PreviewBackgroundKind
{
    Checker,
    White,
    Black,
    Gray,
}

/// <summary>利用者ごとの設定。%APPDATA%\PSDToolViewer\settings.json に保存する。</summary>
public sealed class AppSettings
{
    public const int MaxRecentFiles = 10;

    public bool SwapPanels { get; set; }
    public bool ShowThumbnails { get; set; } = true;
    public ThemeSetting Theme { get; set; } = ThemeSetting.System;
    public PreviewBackgroundKind PreviewBackground { get; set; } = PreviewBackgroundKind.Checker;
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public bool TrimTransparent { get; set; }
    public double LayerPanelWidth { get; set; } = 300;

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public bool WindowMaximized { get; set; }

    public List<string> RecentFiles { get; set; } = [];

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecentFiles) RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PSDToolViewer");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 壊れた設定は既定値で上書きする
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 設定を保存できなくても動作は続ける
        }
    }
}
