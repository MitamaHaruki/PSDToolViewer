# .ptv ファイル形式 仕様書（形式バージョン 1）

PSDTool Viewer の専用形式 `.ptv` の構造と、内容を画像として表示するための合成方法を説明します。
ほかのアプリで `.ptv` を読み込む（または書き出す）ときの資料です。

- 対象：形式バージョン 1（PSDTool Viewer 1.0.0 以降が読み書きする形式）
- 実装：[`PtvFormat.cs`](../src/PSDToolViewer.Core/Formats/Ptv/PtvFormat.cs)（読み書き）、[`Rendering/`](../src/PSDToolViewer.Core/Rendering)（合成）

この仕様書では、次の言葉を使い分けます。

- **書く側**：`.ptv` を作るアプリ。PSDTool Viewer が書くファイルは、この仕様書の「書く側」の規則をすべて満たします
- **読む側**：`.ptv` を読み込むアプリ。PSDTool Viewer は規則から少し外れたファイルも、できる範囲で読み込みます（各項目に記載）

## 1. 全体の構成

`.ptv` は ZIP ファイルです。中には次のエントリがあります。

| エントリ | 内容 | 必須 |
|---|---|---|
| `thumbnail.png` | 保存時の見た目を縮小した画像（[4.3](#43-サムネイルthumbnailpng)） | 任意（PSDTool Viewer は常に入れる） |
| `document.json` | レイヤー構造・名前・表示状態などの情報（[3](#3-documentjson)） | 必須 |
| `layers/00001.png` など | レイヤー画像（[4.1](#41-レイヤー画像)） | `document.json` から参照されるもの |
| `masks/00004.png` など | レイヤーマスク画像（[4.2](#42-マスク画像)） | `document.json` から参照されるもの |

レイヤー構造は PSD（Photoshop 形式）をもとにしています。調整レイヤー、レイヤー効果、文字やシェイプの情報、スマートオブジェクトは含みません（PSD の読み込み時に画像として取り込みます）。

## 2. ZIP コンテナ

- 一般的な ZIP 形式です。ファイルが大きい場合は ZIP64 になることがあります
- 圧縮方式は **無圧縮（0）** と **Deflate（8）** のみを使います。暗号化・分割はしません
- エントリ名は `/` 区切りの相対パス（ASCII、大文字小文字を区別）です
- 読む側は、ZIP の中央ディレクトリからエントリを探してください

PSDTool Viewer は次の順で書きます。

1. `thumbnail.png`（無圧縮）
2. `document.json`（Deflate）
3. レイヤー画像とマスク画像（無圧縮。PNG は圧縮済みのため）

読む側はエントリの順序に依存しないでください。ただし `thumbnail.png` を入れる場合、書く側は **先頭のエントリ・無圧縮** にしてください（エクスプローラーのサムネイル拡張は、無圧縮の `thumbnail.png` だけを読みます）。

上の表にないエントリや、`document.json` から参照されていない画像は無視してください。PSDTool Viewer は読み込み時に無視し、保存し直すと残りません。

## 3. document.json

UTF-8 の JSON です（BOM は付けないでください）。

- プロパティ名は大文字小文字を区別します（camelCase）
- 知らないプロパティは無視してください（将来の拡張用）
- 表の「必須」は、書く側が必ず書くものです。読む側は、省略されたプロパティを既定値として扱ってください（PSDTool Viewer と同じ動作）
- 書く側は `null` を書かず、プロパティごと省略してください（PSDTool Viewer は `visible` などに `null` があると読み込めません）
- 整数は JSON の数値で書きます。描画モードは文字列で書きます

### 3.1 ルート

| プロパティ | 型 | 必須 | 既定値 | 内容 |
|---|---|---|---|---|
| `version` | 整数 | ○ | — | 形式バージョン。現在は `1`。PSDTool Viewer は `1` より大きいファイルを読み込みません |
| `width` | 整数 | ○ | — | キャンバスの幅（px、1 以上） |
| `height` | 整数 | ○ | — | キャンバスの高さ（px、1 以上） |
| `children` | ノードの配列 |  | `[]` | 最上位のレイヤーとフォルダ。**先頭が最前面**（Photoshop のレイヤーパネルと同じ並び） |
| `resources` | 画像リソースの配列 |  | なし | PSD から引き継いだ画像リソース（[3.4](#34-画像リソース)） |

座標はキャンバスの左上を原点とし、右が X、下が Y の正の向きです（単位は px）。

### 3.2 ノード（レイヤーとフォルダ）

レイヤーとフォルダで共通のプロパティです。

| プロパティ | 型 | 必須 | 既定値 | 内容 |
|---|---|---|---|---|
| `type` | 文字列 | ○ | `"layer"` | `"layer"`（レイヤー）または `"group"`（フォルダ）。PSDTool Viewer は `"group"` 以外をレイヤーとして読みます |
| `name` | 文字列 | ○ | `""` | 名前。先頭の `*` `!` の記号を含みます（[5](#5-名前の記号と表示状態)） |
| `visible` | 真偽値 | ○ | `true` | 表示するか |
| `opacity` | 整数 0～255 |  | `255` | 不透明度（255 が 100%） |
| `fillOpacity` | 整数 0～255 |  | `255` | 塗りの不透明度（PSD との相互変換用） |
| `blendMode` | 文字列 | ○ | レイヤーは `"normal"`、フォルダは `"passThrough"` | 描画モード（[3.5](#35-描画モード)） |
| `clipping` | 真偽値 |  | `false` | 下のレイヤーでクリッピングするか（[6.4](#64-クリッピング)） |
| `mask` | マスク |  | なし | レイヤーマスク（[3.3](#33-マスク)） |

レイヤー（`"type": "layer"`）だけのプロパティです。

| プロパティ | 型 | 必須 | 既定値 | 内容 |
|---|---|---|---|---|
| `left` | 整数 | ○ | `0` | 画像の左上の X 座標（負の値やキャンバス外も可） |
| `top` | 整数 | ○ | `0` | 画像の左上の Y 座標 |
| `image` | 文字列 |  | なし | レイヤー画像のエントリ名（例：`"layers/00003.png"`）。ないときは何も描かない空のレイヤー |

フォルダ（`"type": "group"`）だけのプロパティです。

| プロパティ | 型 | 必須 | 既定値 | 内容 |
|---|---|---|---|---|
| `expanded` | 真偽値 | ○ | `true` | レイヤーパネルで開いているか（画面の状態だけで、合成には影響しない） |
| `children` | ノードの配列 | ○ | `[]` | フォルダの中身。先頭が最前面 |

読む側は、エントリが見つからない `image` を空のレイヤーとして扱ってください（PSDTool Viewer と同じ動作）。

### 3.3 マスク

| プロパティ | 型 | 必須 | 既定値 | 内容 |
|---|---|---|---|---|
| `x` | 整数 | ○ | `0` | マスク範囲の左上の X 座標（**キャンバス座標**。レイヤーの位置からの相対ではない） |
| `y` | 整数 | ○ | `0` | マスク範囲の左上の Y 座標 |
| `width` | 整数 | ○ | `0` | マスク範囲の幅（0 も可） |
| `height` | 整数 | ○ | `0` | マスク範囲の高さ（0 も可） |
| `defaultColor` | 整数 0～255 | ○ | `0` | マスク範囲の外の値 |
| `disabled` | 真偽値 |  | `false` | `true` のとき合成に使わない（PSD の「マスクを使用しない」状態。情報としては残す） |
| `image` | 文字列 |  | なし | マスク画像のエントリ名。ないときは範囲内をすべて 0 とする |

マスクの値は 255 が表示、0 が非表示です。中間の値は、その割合だけ不透明度を下げます。

### 3.4 画像リソース

PSD の画像リソースを、PSD に保存し直すときのために引き継ぎます。

| プロパティ | 型 | 必須 | 既定値 | 内容 |
|---|---|---|---|---|
| `id` | 整数 0～65535 | ○ | — | PSD の画像リソース ID |
| `name` | 文字列 | ○ | `""` | 名前（通常は空） |
| `data` | 文字列 | ○ | `""` | 中身のバイト列を Base64 にしたもの |

PSDTool Viewer が PSD から引き継ぐのは次の 2 つです。

| ID | 内容 |
|---|---|
| `1005`（0x03ED） | 解像度情報（ResolutionInfo）。中身は Adobe の PSD 仕様のとおり |
| `1039`（0x040F） | ICC プロファイル。レイヤー画像の色空間を表す（[4.4](#44-色空間)） |

読む側は、知らない ID を無視してください。書く側は、上の 2 つ以外を入れないでください。

### 3.5 描画モード

`blendMode` には次の値を使います。計算式は [6.2](#62-描画モードの計算) にあります。

| 値 | Photoshop での名前 | PSD のキー |
|---|---|---|
| `passThrough` | 通過（フォルダのみ） | `pass` |
| `normal` | 通常 | `norm` |
| `dissolve` | ディザ合成 | `diss` |
| `darken` | 比較（暗） | `dark` |
| `multiply` | 乗算 | `mul ` |
| `colorBurn` | 焼き込みカラー | `idiv` |
| `linearBurn` | 焼き込み（リニア） | `lbrn` |
| `darkerColor` | カラー比較（暗） | `dkCl` |
| `lighten` | 比較（明） | `lite` |
| `screen` | スクリーン | `scrn` |
| `colorDodge` | 覆い焼きカラー | `div ` |
| `linearDodge` | 覆い焼き（リニア）- 加算 | `lddg` |
| `lighterColor` | カラー比較（明） | `lgCl` |
| `overlay` | オーバーレイ | `over` |
| `softLight` | ソフトライト | `sLit` |
| `hardLight` | ハードライト | `hLit` |
| `vividLight` | ビビッドライト | `vLit` |
| `linearLight` | リニアライト | `lLit` |
| `pinLight` | ピンライト | `pLit` |
| `hardMix` | ハードミックス | `hMix` |
| `difference` | 差の絶対値 | `diff` |
| `exclusion` | 除外 | `smud` |
| `subtract` | 減算 | `fsub` |
| `divide` | 除算 | `fdiv` |
| `hue` | 色相 | `hue ` |
| `saturation` | 彩度 | `sat ` |
| `color` | カラー | `colr` |
| `luminosity` | 輝度 | `lum ` |

PSDTool Viewer 1.0 は、表にない値を含むファイルを読み込めません。ほかのアプリでは、知らない値を `normal` として扱うことをおすすめします。

### 3.6 例

```json
{
  "version": 1,
  "width": 1200,
  "height": 1600,
  "children": [
    {
      "type": "group",
      "name": "表情",
      "visible": true,
      "blendMode": "passThrough",
      "expanded": true,
      "children": [
        {
          "type": "layer",
          "name": "*笑顔",
          "visible": true,
          "blendMode": "normal",
          "left": 410,
          "top": 380,
          "image": "layers/00001.png"
        },
        {
          "type": "layer",
          "name": "*真顔",
          "visible": false,
          "blendMode": "normal",
          "left": 412,
          "top": 384,
          "image": "layers/00002.png"
        }
      ]
    },
    {
      "type": "layer",
      "name": "影",
      "visible": true,
      "opacity": 128,
      "blendMode": "multiply",
      "clipping": true,
      "left": 230,
      "top": 520,
      "image": "layers/00003.png"
    },
    {
      "type": "layer",
      "name": "!体",
      "visible": true,
      "blendMode": "normal",
      "left": 200,
      "top": 300,
      "image": "layers/00004.png",
      "mask": {
        "x": 180,
        "y": 1400,
        "width": 900,
        "height": 200,
        "defaultColor": 255,
        "image": "masks/00004.png"
      }
    }
  ],
  "resources": [
    { "id": 1039, "name": "", "data": "AAAMSExpbm8CEAAAbW50clJHQiBYWVogB84AAgAJAAYAMQAA..." }
  ]
}
```

この例では、最前面の「表情」フォルダの中で「笑顔」と「真顔」がラジオボタンになっていて、「笑顔」が選ばれています。「影」は「体」でクリッピングされ、乗算・不透明度 50% で重なります。「体」は強制表示で、下端のマスクで裾が消えています。`resources` の `data` は途中を省略しています。

## 4. 画像

### 4.1 レイヤー画像

- PNG です。PSDTool Viewer は **8bit の RGBA（カラータイプ 6）、ストレートアルファ（乗算済みでない）** で書きます
- 画像の大きさがそのままレイヤーの大きさで、`left` `top` の位置に置きます。キャンバスからはみ出してもかまいません
- 画素の値はドキュメントの色空間の値です（[4.4](#44-色空間)）。読む側は PNG の色に関するチャンク（`iCCP` `sRGB` `gAMA` `cHRM`）を無視して、値をそのまま使ってください
  - PSDTool Viewer が書く PNG には、画像ライブラリ（WIC）が自動で付ける `sRGB` `gAMA` `pHYs` チャンクが入っています。これらは実際の色空間や解像度を表していません
  - これらのチャンクを見て色やガンマを補正するライブラリでは、補正を切ってから読んでください
- 読む側は、RGB・グレースケール・パレット・16bit など、ほかの PNG も受け付けることをおすすめします（PSDTool Viewer は 8bit の RGBA に変換して読みます）
- PSDTool Viewer は PSD の読み込み時に、レイヤー画像の透明な余白を切り詰めます

### 4.2 マスク画像

- PNG の **8bit グレースケール（カラータイプ 0）** です
- 大きさはマスクの `width` × `height` と同じにしてください。PSDTool Viewer は大きさが違うとき、範囲内をすべて 0 として読みます
- マスク範囲が空（幅か高さが 0）のときは画像を入れず、`image` を省きます

### 4.3 サムネイル（thumbnail.png）

- 保存時の表示状態で合成した画像を、長いほうの辺が 512px 以下になるよう縮小したものです（縦横比は保ち、小さい画像は拡大しない）
- **sRGB に変換済み** の 8bit RGBA（ストレートアルファ）です。レイヤー画像と違い、`sRGB` チャンクのとおりの色です
- エクスプローラーのサムネイルに使います。読む側は一覧の表示などに使えますが、正確な表示には `document.json` から合成してください
- 書く側は、内容を変えて保存するときに作り直してください（作れない場合は入れないでください。古い見た目が残ります）

### 4.4 色空間

- `resources` に ID `1039` の ICC プロファイルがあれば、レイヤー画像の色はそのプロファイルの色空間の値です。なければ sRGB とみなします
- 合成はその色空間の値のまま（ガンマを外さずに）行います。Photoshop と同じです
- 画面に表示するときや PNG に書き出すときに、sRGB などの表示用の色空間に変換してください。PSDTool Viewer は合成結果を sRGB に変換して表示します

## 5. 名前の記号と表示状態

[PSDTool](https://oov.github.io/psdtool/) の規則に従い、名前の先頭の記号でレイヤーとフォルダの扱いを決めます。

### 5.1 記号

名前の先頭に続く `*` と `!` を記号として読みます。記号を除いた残りが表示名です。

| 記号 | 意味 |
|---|---|
| `!` | **強制表示**。`visible` にかかわらず常に表示する。利用者は切り替えられない |
| `*` | **ラジオボタン**。同じフォルダ（または最上位）の `*` 付き同士の中で、1 つだけを表示する |
| なし | 利用者が `visible` を切り替えられる |

- `*` と `!` の両方がある場合は `!` を優先し、ラジオボタンとしては扱いません。PSDTool Viewer で記号を切り替えると、どちらか片方だけが付きます（PSD から読み込んだ名前に両方ある場合は、そのまま書きます）
- 例：`*笑顔` → 表示名「笑顔」、ラジオボタン。`!体` → 表示名「体」、強制表示

### 5.2 ラジオボタンの選択

読む側は、読み込んだあと各フォルダ（最上位を含む）で次のように整えてください。

1. ラジオボタン（`*` があり `!` がない子）のうち、先頭（最前面側）から見て最初の `visible` が `true` のものを選ぶ
2. 1 つもなければ、最初のラジオボタンを選ぶ
3. 選んだものだけを `visible: true` にし、ほかのラジオボタンは `false` にする

PSDTool Viewer が書くファイルは、この状態に整えてあります。

### 5.3 実際に表示されるか

ノードは、**自分と、そのすべての親フォルダが「`!` 付き、または `visible` が `true`」** のときに表示されます。

## 6. 合成（表示する画像の作り方）

PSDTool Viewer の合成方法です。Photoshop の見た目に合わせています。

### 6.1 基本

1. `width` × `height` の透明なキャンバスを用意する
2. `children` を **末尾（最背面）から先頭（最前面）の順** に重ねる。表示されないノード（[5.3](#53-実際に表示されるか)）は飛ばす
3. 各ノードの不透明度は `opacity × fillOpacity ÷ 255`（小数点以下切り捨て）とする（レイヤー効果がないため、塗りの不透明度も不透明度と同じに扱う）
4. マスク（`disabled` でないもの）は、各画素の不透明度に「マスクの値 ÷ 255」を掛ける
5. キャンバスの外は捨てる

### 6.2 描画モードの計算

レイヤーの各画素は、[W3C Compositing and Blending Level 1](https://www.w3.org/TR/compositing-1/) の「ブレンドしてから source-over」の式で下の画像に重ねます。色の値は 0～1 で、Cs・αs が重ねる側（αs はレイヤーの α × 不透明度 × マスク）、Cb・αb が下の画像です。

```
Cs' = (1 − αb) × Cs + αb × B(Cb, Cs)
αo  = αs + αb × (1 − αs)
Co  = (αs × Cs' + αb × Cb × (1 − αs)) ÷ αo
```

B(Cb, Cs) は描画モードごとに次のとおりです（RGB の各チャンネルで計算）。

| 値 | B(Cb, Cs) |
|---|---|
| `normal` `dissolve` `passThrough` | Cs（`dissolve` も通常として描く） |
| `multiply` | Cb × Cs |
| `screen` | Cb + Cs − Cb × Cs |
| `overlay` | HardLight(Cs, Cb)（`hardLight` の Cb と Cs を入れ替えたもの） |
| `darken` | min(Cb, Cs) |
| `lighten` | max(Cb, Cs) |
| `colorDodge` | Cb = 0 なら 0、Cs = 1 なら 1、それ以外は min(1, Cb ÷ (1 − Cs)) |
| `colorBurn` | Cb = 1 なら 1、Cs = 0 なら 0、それ以外は 1 − min(1, (1 − Cb) ÷ Cs) |
| `linearBurn` | max(0, Cb + Cs − 1) |
| `linearDodge` | min(1, Cb + Cs) |
| `hardLight` | Cs ≤ 0.5 なら 2 × Cb × Cs、それ以外は Screen(Cb, 2 × Cs − 1) |
| `softLight` | Cs ≤ 0.5 なら 2 × Cb × Cs + Cb² × (1 − 2 × Cs)、それ以外は √Cb × (2 × Cs − 1) + 2 × Cb × (1 − Cs) |
| `vividLight` | Cs ≤ 0.5 なら ColorBurn(Cb, 2 × Cs)、それ以外は ColorDodge(Cb, 2 × (Cs − 0.5)) |
| `linearLight` | Cb + 2 × Cs − 1（0～1 に収める） |
| `pinLight` | Cs ≤ 0.5 なら min(Cb, 2 × Cs)、それ以外は max(Cb, 2 × Cs − 1) |
| `hardMix` | Cb + Cs ≥ 1 なら 1、それ以外は 0 |
| `difference` | \|Cb − Cs\| |
| `exclusion` | Cb + Cs − 2 × Cb × Cs |
| `subtract` | max(0, Cb − Cs) |
| `divide` | Cs = 0 なら（Cb = 0 なら 0、それ以外は 1）、それ以外は min(1, Cb ÷ Cs) |
| `hue` | SetLum(SetSat(Cs, Sat(Cb)), Lum(Cb)) |
| `saturation` | SetLum(SetSat(Cb, Sat(Cs)), Lum(Cb)) |
| `color` | SetLum(Cs, Lum(Cb)) |
| `luminosity` | SetLum(Cb, Lum(Cs)) |
| `darkerColor` | Lum(Cs) < Lum(Cb) なら Cs、それ以外は Cb |
| `lighterColor` | Lum(Cs) > Lum(Cb) なら Cs、それ以外は Cb |

Lum・Sat・SetLum・SetSat は W3C の定義のとおりで、Lum(C) = 0.3 × R + 0.59 × G + 0.11 × B です。

PSDTool Viewer は 8bit の整数で計算します。そのため、小数で計算した結果とは値が少しずれることがあります（多くは 1～2。焼き込みカラーのように変化の急な描画モードや、何枚も重なる部分では 3～5 程度）。

### 6.3 フォルダ

- **`passThrough`（通過）**：子をフォルダの外の画像（下の画像）に直接重ねる。フォルダの不透明度が 255 未満か、マスクがある場合は、「重ねる前」と「重ねた後」を画素ごとに不透明度 × マスクの割合で混ぜる（乗算済みの値で線形に補間する）
- **それ以外**：透明な作業用キャンバスに子を合成し、その結果を 1 枚のレイヤーとして、フォルダの描画モード・不透明度・マスクで下に重ねる

### 6.4 クリッピング

兄弟の並びを背面から見て、`clipping` が `false` のノード（**土台**）と、そのすぐ前面に続く `clipping` が `true` のノードを 1 組とします。最背面のノードは、`clipping` が `true` でも土台として扱います。

- 土台が表示されないときは、組全体を描きません
- クリッピングされたノードがすべて非表示のときは、土台を普通に描きます
- それ以外のときは、次のように描きます
  1. 透明な作業用キャンバスに、土台を通常・不透明度 100% で描く。土台のマスクは掛ける。土台がフォルダなら、子を合成してからフォルダのマスクを掛ける
  2. 表示されているクリッピングされたノードを背面から順に、作業用キャンバスの α を変えずに（W3C の source-atop。Co = αs × Cs' + Cb × (1 − αs)、α は αb のまま）、それぞれの描画モード・不透明度・マスクで重ねる。フォルダは子を別の作業用キャンバスに合成して 1 枚として重ねる。`passThrough` は `normal` として扱う
  3. 作業用キャンバスを、土台の描画モード（`passThrough` は `normal`）と不透明度で下に重ねる

## 7. 書き出すときの注意

ほかのアプリで `.ptv` を作る場合の注意です。

- `version` は `1` にしてください
- 名前には記号（`*` `!`）を含めて書き、ラジオボタンは [5.2](#52-ラジオボタンの選択) の状態に整えてください
- 画像のエントリ名は自由ですが、PSDTool Viewer に合わせて `layers/00000.png` `masks/00000.png` の形をおすすめします。番号は、すべてのノードを先頭から深さ優先でたどった通し番号です（フォルダも番号を 1 つ使います）
- `thumbnail.png` を先頭・無圧縮で入れると、PSDTool Viewer をインストールした環境のエクスプローラーでサムネイルが表示されます

PSDTool Viewer 1.0 は、次のファイルを読み込めません。

- `document.json` がない、または JSON として読めない
- `version` が 1 より大きい
- `width` か `height` が 1 未満
- 型が合わない値（`opacity` が 255 を超える、`visible` が文字列、など）や、表にない `blendMode` がある

## 8. 読み込みの例（Python）

[Pillow](https://pypi.org/project/pillow/) を使って、レイヤーの一覧を表示し、表示されているレイヤーを単純に重ねる例です。不透明度・マスク・描画モード・フォルダ・クリッピングを正しく再現するには、[6](#6-合成表示する画像の作り方) の手順を実装してください。

```python
import io, json, zipfile
from PIL import Image

def prefix(name):
    n = len(name) - len(name.lstrip("*!"))
    return name[:n], name[n:]  # (記号, 表示名)

def is_shown(node):
    marks, _ = prefix(node.get("name") or "")
    return "!" in marks or node.get("visible", True)

def load(path):
    with zipfile.ZipFile(path) as z:
        doc = json.loads(z.read("document.json").decode("utf-8"))
        if doc.get("version", 0) > 1:
            raise ValueError("新しい形式です")
        def png(name):
            return Image.open(io.BytesIO(z.read(name))) if name in z.namelist() else None
        canvas = Image.new("RGBA", (doc["width"], doc["height"]))
        def walk(children, depth):
            for node in reversed(children or []):  # 末尾（最背面）から
                marks, label = prefix(node.get("name") or "")
                state = "" if is_shown(node) else "（非表示）"
                print("  " * depth + label + (f"（{marks}）" if marks else "") + state)
                if not is_shown(node):
                    continue
                if node.get("type") == "group":
                    walk(node.get("children"), depth + 1)
                elif node.get("image") and (image := png(node["image"])):
                    layer = Image.new("RGBA", canvas.size)
                    layer.paste(image.convert("RGBA"), (node.get("left", 0), node.get("top", 0)))
                    canvas.alpha_composite(layer)
        walk(doc.get("children"), 0)
        return doc, canvas

doc, image = load("sample.ptv")
image.save("preview.png")  # ICC プロファイル（resources の 1039）がある場合は、その色空間のままの値
```

## 付録 A. JSON Schema

`document.json` の JSON Schema（Draft 2020-12）です。

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "title": "PSDTool Viewer .ptv document.json (version 1)",
  "type": "object",
  "required": ["version", "width", "height"],
  "properties": {
    "version": { "const": 1 },
    "width": { "type": "integer", "minimum": 1 },
    "height": { "type": "integer", "minimum": 1 },
    "children": { "type": "array", "items": { "$ref": "#/$defs/node" } },
    "resources": { "type": "array", "items": { "$ref": "#/$defs/resource" } }
  },
  "$defs": {
    "byte": { "type": "integer", "minimum": 0, "maximum": 255 },
    "node": {
      "type": "object",
      "required": ["type", "name", "visible", "blendMode"],
      "properties": {
        "type": { "enum": ["layer", "group"] },
        "name": { "type": "string" },
        "visible": { "type": "boolean" },
        "opacity": { "$ref": "#/$defs/byte" },
        "fillOpacity": { "$ref": "#/$defs/byte" },
        "blendMode": { "$ref": "#/$defs/blendMode" },
        "clipping": { "type": "boolean" },
        "mask": { "$ref": "#/$defs/mask" },
        "left": { "type": "integer" },
        "top": { "type": "integer" },
        "image": { "type": "string" },
        "expanded": { "type": "boolean" },
        "children": { "type": "array", "items": { "$ref": "#/$defs/node" } }
      }
    },
    "mask": {
      "type": "object",
      "required": ["x", "y", "width", "height", "defaultColor"],
      "properties": {
        "x": { "type": "integer" },
        "y": { "type": "integer" },
        "width": { "type": "integer", "minimum": 0 },
        "height": { "type": "integer", "minimum": 0 },
        "defaultColor": { "$ref": "#/$defs/byte" },
        "disabled": { "type": "boolean" },
        "image": { "type": "string" }
      }
    },
    "resource": {
      "type": "object",
      "required": ["id", "name", "data"],
      "properties": {
        "id": { "type": "integer", "minimum": 0, "maximum": 65535 },
        "name": { "type": "string" },
        "data": { "type": "string", "contentEncoding": "base64" }
      }
    },
    "blendMode": {
      "enum": [
        "passThrough", "normal", "dissolve", "darken", "multiply", "colorBurn", "linearBurn",
        "darkerColor", "lighten", "screen", "colorDodge", "linearDodge", "lighterColor",
        "overlay", "softLight", "hardLight", "vividLight", "linearLight", "pinLight", "hardMix",
        "difference", "exclusion", "subtract", "divide", "hue", "saturation", "color", "luminosity"
      ]
    }
  }
}
```

## 付録 B. 変更履歴

| 形式バージョン | PSDTool Viewer | 内容 |
|---|---|---|
| 1 | 1.0.0 | 最初の公開版。1.0.0 より前の開発版で保存したファイルには `thumbnail.png` がない（形式バージョンは同じ 1） |
