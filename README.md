# PSDTool Viewer

PSDTool 形式で構成された PSD ファイルを読み込み、表示・編集する Windows 11 用アプリケーションです。

- レイヤー名の先頭の `*` をラジオボタン、`!` を強制表示として扱います（フォルダにも有効）
- PSD・専用形式（`.ptv`）・PNG・フォルダを読み込めます
- ラジオボタン化／強制表示の切替、フォルダ作成、移動、名前変更、削除、元に戻す／やり直し
- PSD・`.ptv` で保存、PSD で書き出し（透明余白のトリミングに対応）
- プレビューをクリップボードにコピー、またはドラッグして `image.png` として出力・貼り付け
- GitHub Releases による自動更新

詳しい仕様は [docs/SPEC.md](docs/SPEC.md) を参照してください。

## 動作環境

- Windows 11（.NET のインストールは不要です）

## インストール

[Releases](https://github.com/MitamaHaruki/PSDToolViewer/releases) から次のどちらかをダウンロードしてください。

- `PSDToolViewer-win-Setup.exe`：インストーラ（管理者権限は不要）
- `PSDToolViewer-win-Portable.zip`：ポータブル版（展開してそのまま実行）

どちらも起動時に新しいバージョンを確認し、更新できます。

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

### リリース

`v1.0.0` のようなタグを push すると、GitHub Actions がビルドし、Velopack でパッケージを作って GitHub Releases に公開します。

## 製作者

三玉春樹 / はるたまプロジェクト（[@MitamaHaruki](https://x.com/MitamaHaruki)）

## ライセンス

[MIT License](LICENSE)
