using Kiriha.Services;
using Kiriha.ViewModels;

namespace Kiriha.Views;

/// <summary>
/// 「タブの選択」ではなく<b>渡されたパス</b>に対して働くファイル操作。
/// </summary>
/// <remarks>
/// ファイル一覧の切り取り / 削除 / 名前の変更は <see cref="TabViewModel"/> の選択ベースのコマンドだが、
/// ツリー・お気に入り・クイックアクセス・アドレスバー・タブから同じ操作を出すときは対象が選択と一致しない。
/// 統一コンテキストメニュー（<see cref="BuildCommonPathEntries"/>）はどの場所から開かれてもここを通す。
/// <para>
/// 元はツリー専用（MainWindow.TreeMenu.cs）にあった実装で、メニュー統一に合わせて場所非依存にした。
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// ドライブ直下（<c>C:\</c>）やネットワーク共有のルート（<c>\\server\share</c>）か。
    /// IFileOperation へ渡すと中身の一括操作になるため変更系を出さない判定。
    /// </summary>
    /// <remarks>
    /// 判定は <see cref="WindowsPathIdentity.IsRoot"/> ただ 1 つ。ドラッグ側にも同じ意図の別実装
    /// （<c>IsDriveRootPath</c>）があったが、そちらだけが正規化を通しており、末尾区切り付きの
    /// UNC ルートで「ドラッグは弾くのにメニューは通す」という食い違いになっていた。
    /// </remarks>
    private static bool IsDriveRoot(string path) => WindowsPathIdentity.IsRoot(path);

    /// <summary>指定パス群をクリップボードへ載せる（切り取り / コピー）。</summary>
    private static void SetPathClipboard(MainWindowViewModel vm, IReadOnlyList<string> paths, bool cut)
    {
        if (paths.Count == 0)
        {
            return;
        }

        if (!ClipboardFileService.SetFiles(paths, cut))
        {
            SetPathStatus(vm, LocalizationService.Text("Text.Clipboard.WriteFailed"));
            return;
        }

        // 貼り付けの活性状態は各タブが持っているので、まとめて再評価させる
        foreach (var tab in vm.Tabs)
        {
            tab.NotifyClipboardChanged();
        }

        SetPathStatus(vm, LocalizationService.Text(cut ? "Text.Clipboard.Cut" : "Text.Clipboard.Copied", paths.Count));
    }

    /// <summary>指定パス群をテキストとしてコピーする（1 行 1 パス、エクスプローラーと同じ引用符付き）。</summary>
    private async Task CopyPathsTextAsync(MainWindowViewModel vm, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || vm.SelectedTab is not { IsSettingsTab: false } tab)
        {
            return;
        }

        await CopyPathTextAsync(tab, string.Join(Environment.NewLine, paths.Select(p => $"\"{p}\"")));
    }

    /// <summary>指定フォルダーへ貼り付ける（一覧の貼り付けと同じ規則：切り取りなら移動、同一フォルダーなら自動リネーム）。</summary>
    private static async Task PasteIntoFolderAsync(MainWindowViewModel vm, string dest)
    {
        var files = ClipboardFileService.GetFiles(out var isCut);
        if (files.Count == 0)
        {
            return;
        }

        var sameDir = !isCut && files.Any(f => WindowsPathIdentity.Instance.Equals(Path.GetDirectoryName(f), dest));
        var result = await Task.Run(() => FileOperationService.CopyOrMove(files, dest, move: isCut, renameOnCollision: sameDir));
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
            {
                SetPathStatus(vm, LocalizationService.Text("Text.Op.PasteFailed", FormatPathOpError(result.NativeErrorCode)));
            }

            return;
        }

        if (isCut)
        {
            ClipboardFileService.Clear();
        }

        await AfterPathMutationAsync(vm);
    }

    /// <summary>
    /// 指定パスの名前をダイアログで変更する。ファイル一覧のインライン編集が使えない場所
    /// （ツリー・お気に入り・クイックアクセス・アドレスバー・タブ）からの「名前の変更」はここを通る。
    /// </summary>
    private async Task RenamePathAsync(MainWindowViewModel vm, string path)
    {
        var oldName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        if (oldName.Length == 0)
        {
            return;
        }

        var newName = await PromptTextAsync(LocalizationService.Text("Text.Command.Rename"), oldName);
        if (newName is null)
        {
            return;
        }

        newName = newName.Trim();
        var dir = Path.GetDirectoryName(path);
        if (newName.Length == 0 || newName == oldName || dir is null)
        {
            return;
        }

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            SetPathStatus(vm, LocalizationService.Text("Text.Rename.InvalidChars"));
            return;
        }

        var newPath = Path.Combine(dir, newName);
        if (Directory.Exists(newPath) || File.Exists(newPath))
        {
            SetPathStatus(vm, LocalizationService.Text("Text.Rename.AlreadyExists", newName));
            return;
        }

        // IFileOperation は同期ブロッキングなので、UI スレッドから呼ぶと固まる
        var result = await Task.Run(() => FileOperationService.Rename(path, newPath));
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
            {
                SetPathStatus(vm, LocalizationService.Text("Text.Op.RenameFailed", FormatPathOpError(result.NativeErrorCode)));
            }

            return;
        }

        // お気に入りは実体名を表示するので、パスが変わったら登録側も追従させる
        vm.UpdateBookmarkPaths(path, newPath);
        await AfterPathMutationAsync(vm);
    }

    /// <summary>指定パス群をごみ箱へ送る（Ctrl+Z で戻せる）。</summary>
    private static async Task DeletePathsAsync(MainWindowViewModel vm, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var recycled = new List<RecycledItem>();
        var result = await Task.Run(() => FileOperationService.DeleteToRecycleBin(paths, permanent: false, recycled));
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
            {
                SetPathStatus(vm, LocalizationService.Text("Text.Op.DeleteFailed", FormatPathOpError(result.NativeErrorCode)));
            }

            return;
        }

        // 一覧の削除と同じく Ctrl+Z で戻せるようにする
        FileUndoService.PushDelete(recycled);
        await AfterPathMutationAsync(vm);
    }

    /// <summary>
    /// パス指定の操作でフォルダー構成を変えた後の反映。ツリーはファイル監視を持たないので明示的に再列挙し、
    /// 表示中のタブは（監視が効かない場合の保険として）フォルダー一覧を読み直す。
    /// </summary>
    private static async Task AfterPathMutationAsync(MainWindowViewModel vm)
    {
        await vm.RefreshSidebarTreeAsync();
        if (vm.SelectedTab is { IsSettingsTab: false } tab)
        {
            RefreshAfterShellOperation(tab);
        }
    }

    private static void SetPathStatus(MainWindowViewModel vm, string text)
    {
        if (vm.SelectedTab is { IsSettingsTab: false } tab)
        {
            tab.StatusText = text;
        }
    }

    /// <summary>ファイル操作エラーを「エラー 206: パスが長すぎます」の形式に整形する。</summary>
    private static string FormatPathOpError(int code)
    {
        var desc = FileOperationService.DescribeError(code);
        return desc.Length > 0
            ? LocalizationService.Text("Text.Error.CodeWithDesc", code, desc)
            : LocalizationService.Text("Text.Error.Code", code);
    }
}
