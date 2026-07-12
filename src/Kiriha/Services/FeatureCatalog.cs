using Kiriha.Models;

namespace Kiriha.Services;

/// <summary>アプリ全体から利用する場所移動・種別絞り込み・一覧操作の定義。</summary>
public static class FeatureCatalog
{
    public static IReadOnlyList<FeatureCommand> All { get; } = Build();

    private static List<FeatureCommand> Build()
    {
        var result = new List<FeatureCommand>(100);
        AddLocations(result);
        AddFilters(result);
        AddActions(result);
        if (result.Count != 100)
        {
            throw new InvalidOperationException($"アプリ機能の定義数が不正です（現在 {result.Count} 件）");
        }

        var validKinds = new HashSet<string>(["location", "filter", "action"], StringComparer.Ordinal);
        if (result.Any(x => !validKinds.Contains(x.Kind) || string.IsNullOrWhiteSpace(x.Value)))
        {
            throw new InvalidOperationException("アプリ機能に未定義の種類または空の値があります");
        }

        var actionValues = new HashSet<string>(
        [
            "clear-filter", "select-all", "select-none", "select-invert", "select-folders", "select-files",
            "sort-name-asc", "sort-name-desc", "sort-modified-desc", "sort-size-desc", "view-Details", "view-List",
            "view-SmallIcons", "view-LargeIcons", "view-ExtraLargeIcons",
        ], StringComparer.Ordinal);
        if (result.Where(x => x.Kind == "action").Any(x => !actionValues.Contains(x.Value)))
        {
            throw new InvalidOperationException("アプリ機能に実行先のない操作があります");
        }

        return result;
    }

    private static void AddLocations(List<FeatureCommand> items)
    {
        (string Title, string Value)[] locations =
        [
            ("ホームへ移動", "UserProfile"), ("デスクトップへ移動", "Desktop"),
            ("ドキュメントへ移動", "MyDocuments"), ("ダウンロードへ移動", "Downloads"),
            ("画像へ移動", "MyPictures"), ("音楽へ移動", "MyMusic"), ("動画へ移動", "MyVideos"),
            ("お気に入りへ移動", "Favorites"), ("最近使った項目へ移動", "Recent"),
            ("送るメニューへ移動", "SendTo"), ("スタートメニューへ移動", "StartMenu"),
            ("スタートアップへ移動", "Startup"), ("テンプレートへ移動", "Templates"),
            ("ローカル AppData へ移動", "LocalApplicationData"), ("Roaming AppData へ移動", "ApplicationData"),
            ("共通 AppData へ移動", "CommonApplicationData"), ("Program Files へ移動", "ProgramFiles"),
            ("Program Files (x86) へ移動", "ProgramFilesX86"), ("Windows フォルダーへ移動", "Windows"),
            ("System32 へ移動", "System"), ("一時フォルダーへ移動", "Temp"),
            ("パブリックへ移動", "Public"), ("OneDrive へ移動", "OneDrive"),
            ("ユーザーフォルダーへ移動", "Users"), ("PC（ドライブ一覧）へ移動", "Computer"),
        ];
        items.AddRange(locations.Select(x => new FeatureCommand(x.Title, "場所", "location", x.Value)));
    }

    private static void AddFilters(List<FeatureCommand> items)
    {
        (string Title, string Extensions)[] filters =
        [
            ("画像", ".jpg;.jpeg;.png;.gif;.bmp;.webp;.tif;.tiff;.heic;.avif"), ("動画", ".mp4;.mkv;.avi;.mov;.wmv;.webm;.m4v"),
            ("音声", ".mp3;.wav;.flac;.aac;.ogg;.m4a;.wma"), ("文書", ".doc;.docx;.odt;.rtf;.txt;.md;.pdf"),
            ("表計算", ".xls;.xlsx;.csv;.ods;.tsv"), ("プレゼンテーション", ".ppt;.pptx;.odp"),
            ("PDF", ".pdf"), ("テキスト", ".txt"), ("Markdown", ".md;.markdown"), ("Word", ".doc;.docx;.docm"),
            ("Excel", ".xls;.xlsx;.xlsm;.xlsb"), ("PowerPoint", ".ppt;.pptx;.pptm"), ("アーカイブ", ".zip;.7z;.rar;.tar;.gz;.bz2;.xz"),
            ("実行ファイル", ".exe;.com;.msi;.msix;.appx"), ("ショートカット", ".lnk;.url"), ("フォント", ".ttf;.otf;.woff;.woff2"),
            ("データベース", ".db;.sqlite;.sqlite3;.mdb;.accdb"), ("設定ファイル", ".json;.yaml;.yml;.toml;.ini;.config;.xml"),
            ("ログ", ".log;.etl;.evtx"), ("バックアップ", ".bak;.backup;.old"), ("一時ファイル", ".tmp;.temp;.part;.crdownload"),
            ("C#", ".cs;.csx"), ("C / C++", ".c;.h;.cpp;.hpp;.cc;.cxx"), ("Java", ".java;.jar;.war"),
            ("JavaScript", ".js;.mjs;.cjs;.jsx"), ("TypeScript", ".ts;.tsx;.mts;.cts"), ("Python", ".py;.pyw;.pyi;.ipynb"),
            ("PowerShell", ".ps1;.psm1;.psd1"), ("シェルスクリプト", ".sh;.bash;.zsh;.fish"), ("バッチ", ".bat;.cmd"),
            ("HTML", ".html;.htm;.xhtml"), ("CSS", ".css;.scss;.sass;.less"), ("XML", ".xml;.xsd;.xsl;.xslt"),
            ("JSON", ".json;.jsonc;.json5"), ("YAML", ".yaml;.yml"), ("Rust", ".rs"), ("Go", ".go"),
            ("Ruby", ".rb;.erb;.gemspec"), ("PHP", ".php;.phtml"), ("Swift", ".swift"), ("Kotlin", ".kt;.kts"),
            ("SQL", ".sql"), ("Vue", ".vue"), ("Svelte", ".svelte"), ("Avalonia / XAML", ".axaml;.xaml"),
            ("ソリューション / プロジェクト", ".sln;.slnx;.csproj;.fsproj;.vbproj"), ("Git 関連", ".gitignore;.gitattributes;.gitmodules"),
            ("Docker 関連", ".dockerfile;.dockerignore"), ("証明書", ".cer;.crt;.pem;.pfx;.p12"), ("ディスクイメージ", ".iso;.img;.vhd;.vhdx"),
            ("3D モデル", ".obj;.fbx;.stl;.gltf;.glb;.3ds"), ("CAD", ".dwg;.dxf;.step;.stp;.iges;.igs"),
            ("電子書籍", ".epub;.mobi;.azw;.azw3;.cbz;.cbr"), ("字幕", ".srt;.vtt;.ass;.ssa"),
            ("メール", ".eml;.msg;.mbox;.pst;.ost"), ("カレンダー / 連絡先", ".ics;.vcf"),
            ("Torrent", ".torrent"), ("レジストリ", ".reg"), ("DLL / ライブラリ", ".dll;.lib;.so;.dylib"),
            ("デバッグシンボル", ".pdb;.dmp;.core"),
        ];
        items.AddRange(filters.Select(x => new FeatureCommand($"{x.Title}で絞り込み", "ファイル種別", "filter", x.Extensions)));
    }

    private static void AddActions(List<FeatureCommand> items)
    {
        (string Title, string Value)[] actions =
        [
            ("絞り込みを解除", "clear-filter"), ("すべて選択", "select-all"), ("選択を解除", "select-none"),
            ("選択を反転", "select-invert"), ("フォルダーだけ選択", "select-folders"), ("ファイルだけ選択", "select-files"),
            ("名前で昇順", "sort-name-asc"), ("名前で降順", "sort-name-desc"), ("更新日時が新しい順", "sort-modified-desc"),
            ("サイズが大きい順", "sort-size-desc"), ("詳細表示", "view-Details"), ("一覧表示", "view-List"),
            ("小アイコン表示", "view-SmallIcons"), ("大アイコン表示", "view-LargeIcons"), ("特大アイコン表示", "view-ExtraLargeIcons"),
        ];
        items.AddRange(actions.Select(x => new FeatureCommand(x.Title, "操作", "action", x.Value)));
    }
}
