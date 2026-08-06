using System.Text;
using System.Text.Json;
using Kiriha.Services;
using Xunit;

namespace Kiriha.Tests;

/// <summary>
/// 失効照会の応答（<c>GET /license/kiriha/check</c>）の読み取り契約。
///
/// この応答で <c>valid: false</c> と判定されたクライアントは、ローカルのライセンスキーを
/// 破棄して試用状態へ戻る。つまり「判定できなかった」を「失効した」と取り違えると、
/// 購入済みユーザーが試用切れのロック画面に閉じ込められる。
/// hub のスキーマ変更や中継の事故で <c>valid</c> を含まない 200 応答が返ることは起こりうるので、
/// 「明示的に false のときだけ失効」を型のレベルで固定する。
/// </summary>
public class LicenseCheckResponseTests
{
    private static LicenseService.LicenseCheckResponse? Parse(string json)
        => JsonSerializer.Deserialize(
            Encoding.UTF8.GetBytes(json),
            LicenseJsonContext.Default.LicenseCheckResponse);

    [Fact]
    public void 明示的なfalseだけが失効として読める()
    {
        Assert.False(Parse("{\"valid\":false}")!.Valid);
    }

    [Fact]
    public void 有効な応答はtrueとして読める()
    {
        Assert.True(Parse("{\"valid\":true}")!.Valid);
    }

    [Fact]
    public void validを含まない応答は判定不能として読める()
    {
        // 空オブジェクトや、フィールド名が変わった応答。false へ倒すとキーを消してしまう。
        Assert.Null(Parse("{}")!.Valid);
        Assert.Null(Parse("{\"ok\":true}")!.Valid);
    }

    [Fact]
    public void validがnullの応答も判定不能として読める()
    {
        Assert.Null(Parse("{\"valid\":null}")!.Valid);
    }
}
