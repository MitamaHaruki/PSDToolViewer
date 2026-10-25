# PSDTool Viewer

PSDTool 形式で構成された PSD ファイルを読み込み、表示・編集する Windows 11 用アプリケーションです。

- レイヤー名の先頭の `*` をラジオボタン、`!` を強制表示として扱います（フォルダにも有効）
- PSD・専用形式（`.ptv`）・PNG・フォルダを読み込めます
- ラジオボタン化／強制表示の切替、フォルダ作成、移動、名前変更、削除、元に戻す／やり直し
- PSD・`.ptv` で保存、PSD で書き出し（透明余白のトリミングに対応）
- プレビューをクリップボードにコピー、またはドラッグして `image.png` として出力・貼り付け
- ホイール・タッチのピンチで拡大縮小
- 埋め込みの色プロファイル（Display P3、Adobe RGB など）を sRGB に変換して表示
- GitHub Releases による自動更新（インストール版）

詳しい仕様は [docs/SPEC.md](docs/SPEC.md) を参照してください。`.ptv` ファイルをほかのアプリで読み込む場合は [docs/PTV_FORMAT.md](docs/PTV_FORMAT.md) を参照してください。

## 動作環境

- Windows 11（.NET のインストールは不要です）

## インストール

[Releases](https://github.com/MitamaHaruki/PSDToolViewer/releases) から、お使いの端末に合うファイルをダウンロードしてください（「設定」→「システム」→「バージョン情報」の「システムの種類」で確認できます）。

| ファイル | 内容 |
|---|---|
| `PSDToolViewer-<版>-win-x64.msi` | インストーラー（Intel・AMD などの x64 端末） |
| `PSDToolViewer-<版>-win-arm64.msi` | インストーラー（Snapdragon などの ARM 端末） |
| `PSDToolViewer-<版>-win-*-portable.zip` | ポータブル版（展開して `PSDToolViewer.exe` を実行） |

インストーラーについて：

- 既定のインストール先は `C:\Program Files\PSDTool Viewer` です（変更できます。管理者権限が必要です）
- `.ptv` ファイルの関連付け、PSD ファイルのサムネイル表示、デスクトップのショートカットは、インストール時に選べます
- エクスプローラーのサムネイルに、`.ptv`・PSD の保存時の見た目を表示します
- アンインストールは「設定」→「アプリ」→「インストールされているアプリ」から行えます
- インストール版は起動時に新しいバージョンを確認し、そのまま更新できます。ポータブル版は新しいバージョンがあるとダウンロードページを案内します

## 開発

必要なもの：.NET 10 SDK

```bash
dotnet build PSDToolViewer.slnx
```

```bash
dotnet test tests/PSDToolViewer.Core.Tests/PSDToolViewer.Core.Tests.csproj
```

```bash
dotnet run --project src/PSDToolViewer
```

### 構成

| フォルダ | 内容 |
|---|---|
| `src/PSDToolViewer.Core` | レイヤー構造、PSD／`.ptv`／PNG の読み書き、合成処理 |
| `src/PSDToolViewer` | WPF アプリ（画面・操作・設定・自動更新） |
| `tests/PSDToolViewer.Core.Tests` | Core の単体テスト |
| `thumbnail` | エクスプローラー用サムネイル拡張（C++） |
| `installer` | MSI インストーラー（WiX）と配布物を作るスクリプト |

### 配布物の作成

インストーラー（MSI）とポータブル版 ZIP を `dist` に作ります。サムネイル拡張のビルドに Visual Studio の C++ ツール（x64・ARM64）が必要です。

```bash
pwsh ./installer/build.ps1
```

### リリース

`v1.0.0` のようなタグを push すると、GitHub Actions が配布物を作って GitHub Releases に公開します。インストール版のアプリは、ここで公開された MSI を使って更新します。

## 製作者

三玉春樹 / はるたまプロジェクト（[@MitamaHaruki](https://x.com/MitamaHaruki)）

## ライセンス

[MIT License](LICENSE)
