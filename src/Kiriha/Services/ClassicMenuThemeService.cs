using System.Runtime.InteropServices;

namespace Kiriha.Services;

/// <summary>
/// Win32 クラシックメニュー（シェルコンテキストメニュー等）のダーク / ライト切替。
/// Microsoft は公開 API を提供していないため、uxtheme.dll の非公開エクスポート
/// （ordinal 135 = SetPreferredAppMode、ordinal 136 = FlushMenuThemes、Windows 10 1809+）を使う。
/// Explorer 自身や多くのサードパーティ製アプリが用いる定番手法。
/// 将来の Windows で取得できなくなった場合は何もしない（従来どおりのライト表示に戻るだけ）。
/// </summary>
internal static partial class ClassicMenuThemeService
{
    // SetPreferredAppMode の引数（Explorer 内部の PreferredAppMode 列挙に対応）
    private const int ForceDark = 2;
    private const int ForceLight = 3;

    private static bool _loadAttempted;
    private static unsafe delegate* unmanaged<int, int> _setPreferredAppMode;
    private static unsafe delegate* unmanaged<void> _flushMenuThemes;

    /// <summary>以後に生成される Win32 メニューの配色を切り替える（表示中のメニューには影響しない）。</summary>
    public static void SetDark(bool dark)
    {
        unsafe
        {
            try
            {
                EnsureLoaded();
                if (_setPreferredAppMode is null || _flushMenuThemes is null)
                {
                    return;
                }

                _setPreferredAppMode(dark ? ForceDark : ForceLight);
                _flushMenuThemes();
            }
            catch (Exception ex)
            {
                Logger.Log($"クラシックメニューのテーマ切替に失敗しました（ライト表示のまま続行）: {ex.Message}", LogLevel.Debug);
            }
        }
    }

    private static unsafe void EnsureLoaded()
    {
        if (_loadAttempted)
        {
            return;
        }

        _loadAttempted = true;
        if (!NativeLibrary.TryLoad("uxtheme.dll", out var module))
        {
            return;
        }

        // 名前なし（ordinal のみ）のエクスポートのため GetProcAddress を序数で直接呼ぶ
        _setPreferredAppMode = (delegate* unmanaged<int, int>)GetProcAddress(module, 135);
        _flushMenuThemes = (delegate* unmanaged<void>)GetProcAddress(module, 136);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetProcAddress(nint module, nint ordinal);
}
