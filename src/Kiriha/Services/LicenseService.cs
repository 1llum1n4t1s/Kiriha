using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Sekisho.Client;

namespace Kiriha.Services;

public enum LicenseState
{
    /// <summary>購入済み（買い切りのためローカルに永続キャッシュ）。</summary>
    Licensed,

    /// <summary>試用期間中（全機能利用可）。</summary>
    Trial,

    /// <summary>試用期間終了（主要機能をロックし購入を案内）。</summary>
    TrialExpired,
}

/// <summary>
/// Sekisho ライセンス基盤（メール OTP 認証 + JWT オフライン検証）による買い切りライセンス管理。
/// ユーザーにアカウント管理をさせない設計: メールアドレス = ライセンスキー。
/// 買い切りのため一度 active な権利を確認したらローカルへ永続キャッシュし、以後はオフラインで動く
/// （返金失効はオンライン時の機会的な再検証で反映する）。
/// </summary>
public static class LicenseService
{
    private const string HubUrl = "https://sekisho.nephilim.jp";
    private const string AppSlug = "kiriha";

    /// <summary>Sekisho の Kiriha 買い切り商品（¥980）の product_id（apps/hub/scripts/seed-products.sql）。</summary>
    private const string ProductId = "6d998f16-f4da-4266-8904-14a789442504";

    private const int TrialDays = 14;
    private const string TrialRegistryKey = @"Software\Kiriha";
    private const string TrialRegistryValue = "TrialStart";

    // hub の JWT_ISSUER は HubUrl と同一（SekishoClient の既定 issuer = baseUrl）
    private static readonly SekishoClient Client = new(HubUrl);
    private static readonly Lock Gate = new();
    private static PersistedLicense _persisted = new();

    public static LicenseState State { get; private set; } = LicenseState.Trial;
    public static string? Email { get; private set; }
    public static int TrialDaysLeft { get; private set; } = TrialDays;

    /// <summary>状態が変わったとき（UI 表示・機能ゲートの再評価用。UI スレッドで発火）。</summary>
    public static event Action? StateChanged;

    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kiriha", "sekisho-license.json");

    /// <summary>起動時に呼ぶ。ローカルキャッシュから即時に状態を決め、裏でオンライン再検証する。</summary>
    public static void Initialize()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                _persisted = JsonSerializer.Deserialize(
                    File.ReadAllText(StatePath), LicenseJsonContext.Default.PersistedLicense) ?? new PersistedLicense();
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("ライセンス情報の読み込みに失敗しました（試用状態で続行）", ex);
        }

        Email = _persisted.Email;
        RecomputeState();

        // 返金失効の反映と refresh トークンの回転。オフラインなら黙ってキャッシュ継続
        if (_persisted.RefreshToken is not null)
        {
            _ = RevalidateAsync();
        }
    }

    private static void RecomputeState()
    {
        if (_persisted.Licensed)
        {
            State = LicenseState.Licensed;
            TrialDaysLeft = 0;
            return;
        }

        var start = GetOrCreateTrialStartUtc();
        var elapsed = (int)Math.Floor((DateTime.UtcNow - start).TotalDays);
        TrialDaysLeft = Math.Max(0, TrialDays - elapsed);
        State = TrialDaysLeft > 0 ? LicenseState.Trial : LicenseState.TrialExpired;
    }

    /// <summary>試用開始日時。ファイルとレジストリの両方に記録し、古い方を採用する（単純な再インストール対策）。</summary>
    private static DateTime GetOrCreateTrialStartUtc()
    {
        var trialFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kiriha", "trial.dat");
        DateTime? fromFile = null, fromRegistry = null;
        try
        {
            if (File.Exists(trialFile)
                && DateTime.TryParse(File.ReadAllText(trialFile).Trim(), null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var f))
            {
                fromFile = f;
            }
        }
        catch { /* 読み取り不可なら他方に任せる */ }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(TrialRegistryKey);
            if (key?.GetValue(TrialRegistryValue) is string s
                && DateTime.TryParse(s, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var r))
            {
                fromRegistry = r;
            }
        }
        catch { /* レジストリ不可なら他方に任せる */ }

        var start = (fromFile, fromRegistry) switch
        {
            (null, null) => DateTime.UtcNow,
            (null, { } r) => r,
            ({ } f, null) => f,
            ({ } f, { } r) => f < r ? f : r,
        };

        var iso = start.ToString("O");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(trialFile)!);
            File.WriteAllText(trialFile, iso);
        }
        catch { /* 片方だけでも記録できていればよい */ }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(TrialRegistryKey);
            key?.SetValue(TrialRegistryValue, iso);
        }
        catch { /* 同上 */ }

        return start;
    }

    /// <summary>認証コード（OTP）をメールへ送る。</summary>
    public static Task RequestOtpAsync(string email, CancellationToken ct = default)
        => Client.RequestOtpAsync(NormalizeEmail(email), ct);

    /// <summary>OTP コードを検証してログインし、権利（購入済みか）を確認・保存する。購入済みなら true。</summary>
    public static async Task<bool> VerifyOtpAsync(string email, string code, CancellationToken ct = default)
    {
        var normalized = NormalizeEmail(email);
        var tokens = await Client.VerifyOtpAsync(normalized, code.Trim(), ct);
        var entitlements = await Client.ValidateAsync(tokens.AccessToken, ct);
        var licensed = SekishoClient.HasEntitlement(entitlements, AppSlug);

        lock (Gate)
        {
            _persisted = new PersistedLicense
            {
                Email = normalized,
                RefreshToken = tokens.RefreshToken,
                AccessToken = tokens.AccessToken,
                Licensed = licensed || _persisted.Licensed && _persisted.Email == normalized,
                UpdatedAtUtc = DateTime.UtcNow.ToString("O"),
            };
            Save();
        }

        Email = normalized;
        RecomputeState();
        NotifyStateChanged();
        return licensed;
    }

    /// <summary>ログイン済みユーザー向けに Stripe Checkout の URL を発行する（購入ボタン用）。</summary>
    public static async Task<string?> CreateCheckoutUrlAsync(CancellationToken ct = default)
    {
        var access = await GetFreshAccessTokenAsync(ct);
        if (access is null)
        {
            return null;
        }

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", access);
        using var res = await http.PostAsJsonAsync(
            $"{HubUrl}/checkout", new CheckoutRequest(ProductId), LicenseJsonContext.Default.CheckoutRequest, ct);
        if (!res.IsSuccessStatusCode)
        {
            Logger.Log($"Checkout URL の発行に失敗: HTTP {(int)res.StatusCode}", LogLevel.Warning);
            return null;
        }

        var payload = await res.Content.ReadFromJsonAsync(LicenseJsonContext.Default.CheckoutResponse, ct);
        return payload?.Url;
    }

    /// <summary>権利の再検証（refresh 回転込み）。返金失効の反映と購入直後の反映に使う。</summary>
    public static async Task<bool> RevalidateAsync(CancellationToken ct = default)
    {
        try
        {
            var refresh = _persisted.RefreshToken;
            if (refresh is null)
            {
                return _persisted.Licensed;
            }

            var tokens = await Client.RefreshAsync(refresh, ct);
            var entitlements = await Client.ValidateAsync(tokens.AccessToken, ct);
            var licensed = SekishoClient.HasEntitlement(entitlements, AppSlug);

            lock (Gate)
            {
                _persisted.RefreshToken = tokens.RefreshToken;
                _persisted.AccessToken = tokens.AccessToken;
                _persisted.Licensed = licensed;
                _persisted.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
                Save();
            }

            RecomputeState();
            NotifyStateChanged();
            return licensed;
        }
        catch (Exception ex)
        {
            // オフライン等。買い切りはローカルキャッシュを信頼して続行する
            Logger.Log($"ライセンスのオンライン再検証をスキップ: {ex.Message}", LogLevel.Debug);
            return _persisted.Licensed;
        }
    }

    private static async Task<string?> GetFreshAccessTokenAsync(CancellationToken ct)
    {
        var refresh = _persisted.RefreshToken;
        if (refresh is null)
        {
            return null;
        }

        try
        {
            var tokens = await Client.RefreshAsync(refresh, ct);
            lock (Gate)
            {
                _persisted.RefreshToken = tokens.RefreshToken;
                _persisted.AccessToken = tokens.AccessToken;
                Save();
            }

            return tokens.AccessToken;
        }
        catch (Exception ex)
        {
            Logger.Log($"アクセストークンの更新に失敗: {ex.Message}", LogLevel.Warning);
            return null;
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(_persisted, LicenseJsonContext.Default.PersistedLicense));
        }
        catch (Exception ex)
        {
            Logger.LogException("ライセンス情報の保存に失敗しました", ex);
        }
    }

    private static void NotifyStateChanged()
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => StateChanged?.Invoke());

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    internal sealed class PersistedLicense
    {
        public string? Email { get; set; }
        public string? RefreshToken { get; set; }
        public string? AccessToken { get; set; }
        public bool Licensed { get; set; }
        public string? UpdatedAtUtc { get; set; }
    }

    internal sealed record CheckoutRequest([property: JsonPropertyName("productId")] string ProductId);

    internal sealed record CheckoutResponse([property: JsonPropertyName("url")] string? Url);
}

[JsonSerializable(typeof(LicenseService.PersistedLicense))]
[JsonSerializable(typeof(LicenseService.CheckoutRequest))]
[JsonSerializable(typeof(LicenseService.CheckoutResponse))]
internal partial class LicenseJsonContext : JsonSerializerContext;
