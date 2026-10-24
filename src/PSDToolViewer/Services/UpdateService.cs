using Velopack;
using Velopack.Sources;

namespace PSDToolViewer.Services;

/// <summary>GitHub Releases を使った自動更新（Velopack）。</summary>
public sealed class UpdateService
{
    public const string RepositoryUrl = "https://github.com/MitamaHaruki/PSDToolViewer";

    private readonly UpdateManager _manager = new(new GithubSource(RepositoryUrl, null, false));
    private UpdateInfo? _pending;

    /// <summary>インストール版（またはポータブル版）として起動しているか。開発中のビルドでは false。</summary>
    public bool IsInstalled => _manager.IsInstalled;

    public string? CurrentVersion => _manager.CurrentVersion?.ToString();

    /// <summary>新しいバージョンがあればその番号、なければ null。</summary>
    public async Task<string?> CheckAsync()
    {
        if (!_manager.IsInstalled) return null;
        _pending = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        return _pending?.TargetFullRelease.Version.ToString();
    }

    public async Task DownloadAsync(Action<int> progress, CancellationToken cancellationToken)
    {
        if (_pending is null) throw new InvalidOperationException("更新が見つかっていません。");
        await _manager.DownloadUpdatesAsync(_pending, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>ダウンロード済みの更新を適用して再起動する（このプロセスは終了する）。</summary>
    public void ApplyAndRestart()
    {
        if (_pending is null) return;
        _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }
}
