using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Kiriha.Services;

public enum LicenseState
{
    /// <summary>購入済み（署名付きキーをローカル検証済み）。</summary>
    Licensed,

    /// <summary>試用期間中（全機能利用可）。</summary>
    Trial,

    /// <summary>試用期間終了（ロックし購入を案内）。</summary>
    TrialExpired,

    /// <summary>キーは有効だがオフライン猶予（30 日）を超過。オンライン失効確認が必要。</summary>
    OnlineCheckRequired,
}

/// <summary>
/// 署名付きライセンスキーによる買い切りライセンス管理（外部ライセンス基盤に依存しない）。
///
/// キー形式: KIRIHA-&lt;base64url(payload JSON)&gt;.&lt;base64url(ECDSA P-256 署名)&gt;
///   payload: {"e":"メールアドレス","p":"購入ID","d":"発行日時"}
/// 署名はアプリ埋め込みの公開鍵でオフライン検証する（秘密鍵は配信 Worker 側のみが保持）。
///
/// 失効（返金）はベストエフォート: 起動時に配信 Worker の失効リストを照会し、
/// オンライン確認が 30 日間成功しなかった場合のみ再確認を要求する。
/// 時計の巻き戻し対策として「観測した最大時刻」を保持し、現在時刻はその値を下回らない。
/// </summary>
public static class LicenseService
{
    private const string BaseUrl = "https://kiriha.nephilim.jp";

    /// <summary>署名検証用の公開鍵（ECDSA P-256, SubjectPublicKeyInfo）。秘密鍵は dev\Secret\kiriha-license。</summary>
    private const string PublicKeySpki =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAELtl4i+aIcNlLv6NP5aT/PhiXae6kVUnPn6DhIb2cMI4x17AhLEr5pNtb2WSPTV5VTcnVUTR4j8naA2unoR9+jQ==";

    private const string KeyPrefix = "KIRIHA-";
    private const int TrialDays = 14;
    private const int OfflineGraceDays = 30;
    private const string TrialRegistryKey = @"Software\Kiriha";
    private const string TrialRegistryValue = "TrialStart";

    private static readonly Lock Gate = new();
    private static PersistedLicense _persisted = new();

    public static LicenseState State { get; private set; } = LicenseState.Trial;
    public static string? Email { get; private set; }
    public static int TrialDaysLeft { get; private set; } = TrialDays;

    /// <summary>購入ページ（Stripe Payment Link）。決済完了ページでキーが即時発行される。</summary>
    public static string PurchaseUrl => $"{BaseUrl}/buy";

    /// <summary>状態が変わったとき（UI 表示・ロック再評価用。UI スレッドで発火）。</summary>
    public static event Action? StateChanged;

    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kiriha", "license.json");

    /// <summary>起動時に呼ぶ。ローカルのキー検証で即時に状態を決め、裏で失効リストを照会する。</summary>
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

        // 時計巻き戻し対策: 観測済み最大時刻を進める（保存は状態確定後にまとめて行う）
        var now = EffectiveUtcNow();
        lock (Gate)
        {
            _persisted.MaxSeenUtc = now.ToString("O");
            Save();
        }

        RecomputeState();

        if (_persisted.Key is not null)
        {
            _ = CheckRevocationAsync();
        }
    }

    /// <summary>「観測した最大時刻」を下回らない現在時刻（UTC）。</summary>
    private static DateTime EffectiveUtcNow()
    {
        var now = DateTime.UtcNow;
        if (DateTime.TryParse(_persisted.MaxSeenUtc, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var seen) && seen > now)
        {
            return seen;
        }

        return now;
    }

    private static void RecomputeState()
    {
        if (_persisted.Key is not null && TryParseAndVerify(_persisted.Key, out var payload))
        {
            Email = payload.Email;

            // オンライン失効確認が猶予期間を超えて成功していなければ再確認を要求する
            var lastCheck = ParseUtc(_persisted.LastOnlineCheckUtc) ?? ParseUtc(_persisted.ActivatedAtUtc);
            State = lastCheck is { } t && (EffectiveUtcNow() - t).TotalDays <= OfflineGraceDays
                ? LicenseState.Licensed
                : LicenseState.OnlineCheckRequired;
            TrialDaysLeft = 0;
            return;
        }

        Email = null;
        var start = GetOrCreateTrialStartUtc();
        var elapsed = (int)Math.Floor((EffectiveUtcNow() - start).TotalDays);
        TrialDaysLeft = Math.Max(0, TrialDays - elapsed);
        State = TrialDaysLeft > 0 ? LicenseState.Trial : LicenseState.TrialExpired;
    }

    private static DateTime? ParseUtc(string? value)
        => DateTime.TryParse(value, null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var t)
            ? t
            : null;

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

    /// <summary>ライセンスキーを検証して有効化する。成功したら true（オフラインでも完結する）。</summary>
    public static bool ActivateKey(string key)
    {
        var trimmed = key.Trim();
        if (!TryParseAndVerify(trimmed, out var payload))
        {
            return false;
        }

        var now = DateTime.UtcNow.ToString("O");
        lock (Gate)
        {
            _persisted = new PersistedLicense
            {
                Key = trimmed,
                MaxSeenUtc = now,
                ActivatedAtUtc = now,
                LastOnlineCheckUtc = now,
            };
            Save();
        }

        Email = payload.Email;
        RecomputeState();
        NotifyStateChanged();

        // 有効化直後にも失効リストを照会しておく（返金済みキーの使い回し対策）
        _ = CheckRevocationAsync();
        return true;
    }

    /// <summary>キーの形式と署名を検証する。</summary>
    private static bool TryParseAndVerify(string key, out LicensePayload payload)
    {
        payload = new LicensePayload();
        try
        {
            if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            // base64url 自体が '-' を含むため、payload と署名の区切りは '.'（JWT 風）
            var parts = key[KeyPrefix.Length..].Split('.');
            if (parts.Length != 2)
            {
                return false;
            }

            var payloadBytes = FromBase64Url(parts[0]);
            var signature = FromBase64Url(parts[1]);

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeySpki), out _);
            if (!ecdsa.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256))
            {
                return false;
            }

            var parsed = JsonSerializer.Deserialize(payloadBytes, LicenseJsonContext.Default.LicensePayload);
            if (parsed?.Email is not { Length: > 0 } || parsed.PurchaseId is not { Length: > 0 })
            {
                return false;
            }

            payload = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    /// <summary>失効リストの照会。成功したら猶予期間を更新し、失効していたらライセンスを無効化する。</summary>
    public static async Task<bool> CheckRevocationAsync(CancellationToken ct = default)
    {
        string? purchaseId = null;
        if (_persisted.Key is { } key && TryParseAndVerify(key, out var payload))
        {
            purchaseId = payload.PurchaseId;
        }

        if (purchaseId is null)
        {
            return false;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var res = await http.GetAsync(
                $"{BaseUrl}/license/check?id={Uri.EscapeDataString(purchaseId)}", ct);
            if (!res.IsSuccessStatusCode)
            {
                // サーバー側の一時異常は失効と区別が付かないため猶予を消費するだけに留める
                Logger.Log($"ライセンス失効確認が HTTP {(int)res.StatusCode}（猶予期間で継続）", LogLevel.Debug);
                return true;
            }

            var check = await res.Content.ReadFromJsonAsync(LicenseJsonContext.Default.LicenseCheckResponse, ct);
            if (check is { Valid: false })
            {
                // 返金等で失効。ローカルのライセンスを破棄して試用状態へ戻す
                Logger.Log("ライセンスが失効しています（返金等）。ローカルのキーを無効化します", LogLevel.Warning);
                lock (Gate)
                {
                    _persisted = new PersistedLicense { MaxSeenUtc = DateTime.UtcNow.ToString("O") };
                    Save();
                }

                RecomputeState();
                NotifyStateChanged();
                return false;
            }

            var previousState = State;
            lock (Gate)
            {
                _persisted.LastOnlineCheckUtc = DateTime.UtcNow.ToString("O");
                Save();
            }

            RecomputeState();
            if (State != previousState)
            {
                NotifyStateChanged();
            }

            return true;
        }
        catch (Exception ex)
        {
            // オフライン等。猶予期間内はローカル検証を信頼して続行する
            Logger.Log($"ライセンス失効確認をスキップ: {ex.Message}", LogLevel.Debug);
            return true;
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

    internal sealed class PersistedLicense
    {
        public string? Key { get; set; }

        /// <summary>時計巻き戻し対策: これまでに観測した最大の UTC 時刻。</summary>
        public string? MaxSeenUtc { get; set; }

        public string? ActivatedAtUtc { get; set; }

        /// <summary>失効リスト照会に最後に成功した UTC 時刻（オフライン猶予の起点）。</summary>
        public string? LastOnlineCheckUtc { get; set; }
    }

    internal sealed class LicensePayload
    {
        [JsonPropertyName("e")]
        public string? Email { get; set; }

        [JsonPropertyName("p")]
        public string? PurchaseId { get; set; }

        [JsonPropertyName("d")]
        public string? IssuedAt { get; set; }
    }

    internal sealed record LicenseCheckResponse([property: JsonPropertyName("valid")] bool Valid);
}

[JsonSerializable(typeof(LicenseService.PersistedLicense))]
[JsonSerializable(typeof(LicenseService.LicensePayload))]
[JsonSerializable(typeof(LicenseService.LicenseCheckResponse))]
internal partial class LicenseJsonContext : JsonSerializerContext;
