using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kiriha.Models;

/// <summary>お気に入りの 1 要素（Children が非 null ならフォルダー）。settings.json に永続化する。</summary>
public sealed partial class BookmarkNode : ObservableObject
{
    /// <summary>
    /// 実体を持たないノードの表示名（お気に入り内のグループ分けフォルダーと「PC」）。
    /// </summary>
    /// <remarks>
    /// 実パスを指すリンク項目では<b>使わない</b>。名前は <see cref="DisplayName"/> が実体から導く。
    /// 出荷済みの settings.json には独自に付けた名前が入っているが、それも無視される
    /// （フォルダー名を変えたらお気に入りの表示も追従してほしい、という判断。逆に独自名を残すと、
    /// リネーム後に実体と食い違った名前が残り続けてどれがどれだか分からなくなる）。
    /// </remarks>
    public string Name { get; set; } = "";

    private string? _path;

    /// <summary>リンク先パス。フォルダーノードでは null。</summary>
    /// <remarks>
    /// <b>絶対に <c>[ObservableProperty]</c> にしない。</b>ソースジェネレーターは互いの出力を見られないので、
    /// MVVM Toolkit が生成したプロパティは System.Text.Json のジェネレーター
    /// （<c>SettingsJsonContext</c>）から見えず、<b>settings.json から Path が丸ごと消える</b>。
    /// 実際にそれをやって、次の保存で登録済みお気に入りのリンク先を全部失った（2026-08-08）。
    /// 変更通知は <see cref="DisplayName"/>（実体名の表示）に要るので、手書きで出す。
    /// <c>BookmarkNodeSerializationTests</c> がこの往復を固定している。
    /// </remarks>
    public string? Path
    {
        get => _path;
        set
        {
            if (_path == value)
            {
                return;
            }

            _path = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    /// <summary>子ノード（フォルダーの場合のみ）。</summary>
    public List<BookmarkNode>? Children { get; set; }

    public bool IsFolder => Children is not null;

    /// <summary>ツリーへ出す名前。リンク項目は実体のファイル名 / フォルダー名から導く。</summary>
    /// <remarks>
    /// ドライブ直下（<c>C:\</c>）はファイル名部分が空になるので、パスをそのまま出す。
    /// 「PC」は Path が空文字（<c>FileSystemService.ComputerPath</c>）なので <see cref="Name"/> 側へ落ちる。
    /// </remarks>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (Path is not { Length: > 0 } path)
            {
                return Name;
            }

            return System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar))
                is { Length: > 0 } name ? name : path;
        }
    }

    // 以下は表示用で settings.json には出さない（[property: JsonIgnore]）。
    // アイコンの解決は Directory.Exists やシェル呼び出しを伴いバックグラウンドで行うため、
    // 後から届いた結果をツリーへ反映できるよう変更通知付きにしてある
    // （コレクションを作り直す方式にすると、フォルダーノードの開閉状態が毎回失われる）。

    /// <summary>絵文字アイコンセットでの表示文字。画像アイコンが無いときの表示でもある。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private string _icon = "📁";

    /// <summary>アイコンセット設定に応じた画像アイコン（Material / Windows Shell）。null なら絵文字で描画する。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(HasIconImage))]
    private Avalonia.Media.IImage? _iconImage;

    /// <summary>IconImage をこの項目が所有するか（Windows Shell アイコンは所有、Material はキャッシュ共有で解放禁止）。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _ownsIconImage;

    /// <summary>リンク先の実体がフォルダーか（お気に入りにはファイルも置ける）。
    /// 判定はアイコン解決と同じバックグラウンド処理で行う。既定は true で、
    /// 未解決のうちに選ばれてもフォルダーとして扱う（大半がフォルダーのため）。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isDirectoryTarget = true;

    /// <summary>ドラッグ中に、この項目のどこへ落とそうとしているかの目印。</summary>
    [ObservableProperty]
    [property: JsonIgnore]
    [NotifyPropertyChangedFor(nameof(IsDropBefore))]
    [NotifyPropertyChangedFor(nameof(IsDropAfter))]
    [NotifyPropertyChangedFor(nameof(IsDropInto))]
    private BookmarkDropMark _dropMark;

    [JsonIgnore]
    public bool IsDropBefore => DropMark == BookmarkDropMark.Before;

    [JsonIgnore]
    public bool IsDropAfter => DropMark == BookmarkDropMark.After;

    [JsonIgnore]
    public bool IsDropInto => DropMark == BookmarkDropMark.Into;

    [JsonIgnore]
    public bool HasIconImage => IconImage is not null;

    /// <summary>表示アイコンと実体種別を差し替える。所有していた Shell アイコンはここで解放する。</summary>
    public void SetIcon(string icon, Avalonia.Media.IImage? image, bool ownsImage, bool isDirectory)
    {
        IsDirectoryTarget = isDirectory;
        if (OwnsIconImage && IconImage is Avalonia.Media.Imaging.Bitmap bitmap && !ReferenceEquals(bitmap, image))
        {
            bitmap.Dispose();
        }

        Icon = icon;
        IconImage = image;
        OwnsIconImage = ownsImage && image is not null;
    }
}
