namespace Kiriha.Services;

/// <summary>
/// アプリの永続データ置き場（ファイル / HKCU レジストリ）の唯一の正本。
///
/// 既定は実ユーザーの %LocalAppData%\Kiriha と HKCU 直下で、製品コードからは差し替えない。
/// 自動テストが実ユーザーの設定・ライセンス状態・スタートアップ登録を壊さずに検証できるよう、
/// テストからだけ置き場を退避先へ差し替えられるようにしてある。
/// </summary>
internal static class AppStoragePaths
{
    private static string? _directoryOverride;
    private static string _registryPrefix = "";

    /// <summary>設定・ライセンス等の永続ファイルを置くディレクトリ。</summary>
    public static string Directory
        => _directoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kiriha");

    /// <summary>HKCU 配下のキーパスへ付ける接頭辞。既定は空文字（＝実キーを直接指す）。</summary>
    public static string RegistryPrefix => _registryPrefix;

    /// <summary>HKCU 配下のキーパスを接頭辞付きで解決する。</summary>
    public static string RegistryPath(string relativePath)
        => _registryPrefix.Length == 0 ? relativePath : _registryPrefix + relativePath;

    /// <summary>テスト専用: 置き場を差し替える。null / 空文字を渡すと既定へ戻る。</summary>
    internal static void OverrideForTests(string? directory, string? registryPrefix)
    {
        _directoryOverride = string.IsNullOrEmpty(directory) ? null : directory;
        _registryPrefix = string.IsNullOrEmpty(registryPrefix)
            ? ""
            : registryPrefix.EndsWith('\\') ? registryPrefix : registryPrefix + '\\';
    }
}
