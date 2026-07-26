using System.Runtime.InteropServices.Marshalling;

namespace Kiriha.Services;

/// <summary>
/// source-generated COM（<see cref="StrategyBasedComWrappers"/>）が返す RCW の後始末。
/// </summary>
internal static class ComRelease
{
    /// <summary>
    /// RCW が保持している COM 参照を明示的に落とす。COM オブジェクトを使い終えたら必ずこれを呼ぶ。
    ///
    /// 生成される RCW の実体は <see cref="ComObject"/> で、<see cref="IDisposable"/> を実装していない。
    /// そのため <c>(obj as IDisposable)?.Dispose()</c> や <c>using</c> は無言で何もせず、参照が GC まで残る。
    /// これは実害があり、WIC ビットマップのロックが解放されないまま残って Media Foundation が
    /// 次のフレームを書き込めなくなる（動画の絵が止まる）不具合を実際に踏んでいる。
    ///
    /// <c>Marshal.Release</c> が落とすのは <c>GetOrCreateObjectForComInstance</c> へ渡した生ポインタ側の
    /// 参照であって、RCW が別に持つ参照ではない。両方が必要。
    ///
    /// 呼んだ後の RCW は使用できない。まだ誰かが参照している可能性がある間は呼ばないこと。
    /// </summary>
    internal static void Release(object? comObject)
        => (comObject as ComObject)?.FinalRelease();
}
