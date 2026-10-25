using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace PSDToolViewer.Services;

/// <summary>
/// GitHub Releases を使った更新。最新リリースの MSI をダウンロードし、アプリを終了してから上書きインストールする。
/// </summary>
public sealed class UpdateService
{
    public const string Repository = "MitamaHaruki/PSDToolViewer";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";

    /// <summary>インストーラーがインストール先を書き込むレジストリ（HKLM）。</summary>
    public const string RegistryKeyPath = @"Software\PSDToolViewer";

    private static readonly HttpClient Http = CreateClient();
    private Release? _pending;

    private sealed record Release(Version Version, string PageUrl, string? InstallerUrl, string? InstallerName);

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static string CurrentVersionText => CurrentVersion.ToString(3);

    /// <summary>このプロセスの CPU に合うインストーラー名の一部（x64 / arm64）。</summary>
    public static string Architecture => RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>インストーラーで導入したものとして動いているか（ZIP 版や開発中のビルドは false）。</summary>
    public static bool IsInstalled
    {
        get
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = root.OpenSubKey(RegistryKeyPath);
                if (key?.GetValue("InstallDir") is not string dir || dir.Length == 0) return false;
                return string.Equals(Normalize(dir), Normalize(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    /// <summary>新しいリリースのページ（ブラウザで開く用）。</summary>
    public string PendingPageUrl => _pending?.PageUrl ?? ReleasesPage;

    /// <summary>このままインストールできる更新があるか。</summary>
    public bool CanInstallPending => IsInstalled && _pending?.InstallerUrl is not null;

    /// <summary>新しいバージョンがあればその番号、なければ null。</summary>
    public async Task<string?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null; // まだリリースがない
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsed)) return null;
        var latest = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        if (latest <= CurrentVersion) return null;

        string expected = $"PSDToolViewer-{latest.ToString(3)}-win-{Architecture}.msi";
        string? url = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (string.Equals(asset.GetProperty("name").GetString(), expected, StringComparison.OrdinalIgnoreCase))
                {
                    url = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
        }
        var page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? ReleasesPage : ReleasesPage;
        _pending = new Release(latest, page, url, expected);
        return latest.ToString(3);
    }

    /// <summary>更新用の MSI を一時フォルダにダウンロードし、そのパスを返す。</summary>
    public async Task<string> DownloadAsync(IProgress<int> progress, CancellationToken cancellationToken = default)
    {
        if (_pending is not { InstallerUrl: { } url, InstallerName: { } name })
            throw new InvalidOperationException("ダウンロードできる更新がありません。");

        var dir = Path.Combine(Path.GetTempPath(), "PSDToolViewer", "update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                done += read;
                if (total is > 0) progress.Report((int)(done * 100 / total.Value));
            }
        }
        progress.Report(100);
        return path;
    }

    /// <summary>
    /// このアプリの終了を待ってから MSI で上書きインストールし、終わったらアプリを起動し直す。
    /// 呼び出し後はすぐにアプリを終了すること。
    /// </summary>
    public static void StartInstallAndRestart(string msiPath)
    {
        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "PSDToolViewer.exe");
        // msiexec は必要に応じて自分で管理者の確認を出す。アプリは管理者権限なしで起動し直す
        var script = $$"""
            $p = Get-Process -Id {{Environment.ProcessId}} -ErrorAction SilentlyContinue
            if ($p) { $p.WaitForExit(60000) | Out-Null }
            Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/i', ('"' + {{Quote(msiPath)}} + '"'), '/passive', '/norestart') -Wait
            Start-Process -FilePath {{Quote(exe)}}
            """;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    public static void OpenInBrowser(string url)
        => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"PSDToolViewer/{CurrentVersionText}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
