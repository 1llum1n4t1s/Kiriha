# Material Icon Theme

このディレクトリのアイコン（`icons/*.png`）と `manifest.json` の元データは
[material-icon-theme](https://github.com/material-extensions/vscode-material-icon-theme)
（npm パッケージ `material-icon-theme` v5.36.1）から取得したものです。

- ライセンス: MIT（[LICENSE-material-icon-theme.txt](LICENSE-material-icon-theme.txt) 参照）
- `manifest.json` は同パッケージの `dist/material-icons.json`（VS Code アイコンテーマのマニフェスト形式）を
  Kiriha 用に変換したもの（キーを一部小文字化し、`light` セクションを `light*` プレフィックスへフラット化）。
- `icons/*.png` は元の SVG（1,250 個）を [resvg](https://github.com/linebender/resvg) v0.47.0 で
  256×256 にラスタライズしたもの。Avalonia.Svg.Skia（Avalonia 11 系向けビルドしか存在しない）を
  Avalonia 12 の Kiriha で使うと実機でウィンドウが描画されなくなる致命的な非互換があったため、
  実績のある Bitmap 読み込み経路に統一する目的でビルド時変換に切り替えた。
