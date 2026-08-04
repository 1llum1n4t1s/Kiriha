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
/// 署名はアプリ埋め込みの公開鍵でオフライン検証する（秘密鍵は Sekisho hub 側のみが保持）。
///
/// 失効（返金）はベストエフォート: 起動時に Sekisho hub の失効リストを照会し、
/// オンライン確認が 30 日間成功しなかった場合のみ再確認を要求する。
/// 時計の巻き戻し対策として「観測した最大時刻」を保持し、現在時刻はその値を下回らない。
/// </summary>
public static class LicenseService
{
    private const string BaseUrl = "https://sekisho.kagayoi.com";

    /// <summary>
    /// 失効照会の URL（優先順）。ライセンス基盤（Sekisho hub）へ直接照会し、
    /// hub ドメインの移転・喪失時にも出荷済みクライアントが 30 日後に恒久ロックされないよう、
    /// 自前ドメインの互換プロキシ（kiriha.kagayoi.com → hub へ転送）をフォールバックに持つ。
    /// </summary>
    private static readonly string[] CheckUrls =
    [
        $"{BaseUrl}/license/kiriha/check",
        "https://kiriha.kagayoi.com/license/check",
    ];

    /// <summary>
    /// 署名検証用の公開鍵（ECDSA P-256, SubjectPublicKeyInfo）。秘密鍵は dev\Secret\kiriha-license。
    /// 鍵ローテーション（漏洩時の切り替え等）に備えて複数持てる配列にしてあり、
    /// いずれかの鍵で検証が通れば有効。新鍵へ移行するときは先頭へ追加し、旧鍵は
    /// 既発行キーの検証用に残す（キー形式自体は変えない）。
    /// </summary>
    private static readonly string[] PublicKeysSpki =
    [
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAELtl4i+aIcNlLv6NP5aT/PhiXae6kVUnPn6DhIb2cMI4x17AhLEr5pNtb2WSPTV5VTcnVUTR4j8naA2unoR9+jQ==",
    ];

    private const string KeyPrefix = "KIRIHA-";
    private const int TrialDays = 14;
    private const int OfflineGraceDays = 30;
    // 置き場は AppStoragePaths が正本（テストが実ユーザーのライセンス状態を壊さないよう差し替え可能）。
    private static string TrialRegistryKey => AppStoragePaths.RegistryPath(@"Software\Kiriha");
    private const string TrialRegistryValue = "TrialStart";

    private static readonly Lock Gate = new();
    private static PersistedLicense _persisted = new();

    public static LicenseState State { get; private set; } = LicenseState.Trial;
    public static string? Email { get; private set; }
    public static int TrialDaysLeft { get; private set; } = TrialDays;

    /// <summary>購入ページ（Stripe Payment Link）。決済完了ページでキーが即時発行される。</summary>
    public static string PurchaseUrl => $"{BaseUrl}/buy/kiriha";

    /// <summary>状態が変わったとき（UI 表示・ロック再評価用。UI スレッドで発火）。</summary>
    public static event Action? StateChanged;

    private static string StatePath => Path.Combine(AppStoragePaths.Directory, "license.json");

    /// <summary>試用開始日時の記録先ファイル（レジストリと二重に記録して古い方を採用する）。</summary>
    private static string TrialFilePath => Path.Combine(AppStoragePaths.Directory, "trial.dat");

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
        // State / Email / TrialDaysLeft は組で意味を持つため、起動時の自動失効確認と
        // UI からの再確認が並走しても不整合な組み合わせ（例: Licensed なのに Email 空）を
        // 観測させないよう、3 つの代入を Gate で 1 かたまりにする。
        lock (Gate)
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
        var trialFile = TrialFilePath;
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
            // LastOnlineCheckUtc は「失効照会に成功した時刻」なので、ここでは立てない
            // （有効化はオフラインでも完結するため、成功したとは限らない）。オフライン猶予の
            // 起点は RecomputeState が ActivatedAtUtc で代用するので、猶予の長さは変わらない。
            _persisted = new PersistedLicense
            {
                Key = trimmed,
                MaxSeenUtc = now,
                ActivatedAtUtc = now,
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

    /// <summary>
    /// この PC のライセンス認証を解除して未認証へ戻す（購入自体は無効にならない）。
    ///
    /// 主な用途は認証まわりの動作確認。何台でも使える買い切りなので解除に制限は設けず、
    /// 同じメールアドレスと確認コードでいつでも認証し直せる。
    /// 試用開始日（trial.dat / レジストリ）は消さない。消すと解除するたびに試用期間が
    /// 復活してしまい、期限切れ後の挙動を確認できなくなるうえ、試用の使い回しにもなる。
    /// </summary>
    public static void Deactivate()
    {
        lock (Gate)
        {
            if (_persisted.Key is null)
            {
                return;
            }

            // 時計の巻き戻し対策として観測済み最大時刻だけは引き継ぐ。
            _persisted = new PersistedLicense { MaxSeenUtc = _persisted.MaxSeenUtc };
            Save();
        }

        Logger.Log("ライセンス認証を解除しました（この PC のみ。購入は有効なまま）", LogLevel.Info);
        RecomputeState();
        NotifyStateChanged();
    }

    // ===== メールアドレス + 確認コードでの認証（機種変更・2 台目） =====
    //
    // ライセンスキーは「決済完了ページに 1 度だけ出る文字列」なので、別の PC で使うときに
    // 手元に無いことが多い。そこで購入時のメールアドレスへ 6 桁コードを送り、コードと引き換えに
    // hub が同じ署名キーを作り直して返す経路を用意する（利用者はキーを一切見ない）。
    //
    // メールアドレスだけで通すことはしない。メールアドレスは秘密ではないので、それ単独を
    // 認証情報にすると「アドレスを知っている人＝ライセンスを取得できる人」になってしまう。
    // 「そのメールを受信できる」ことまで確かめて初めて本人とみなす。
    // 台数の制限は引き続き掛けない（買い切り 1 本で何台でも使える方針）。

    private const string RecoverRequestUrl = $"{BaseUrl}/license/kiriha/recover/request";
    private const string RecoverRedeemUrl = $"{BaseUrl}/license/kiriha/recover";

    /// <summary>確認コード送信の結果。</summary>
    public enum RecoveryRequestResult
    {
        Sent,

        /// <summary>メールアドレスの形式が正しくない。</summary>
        InvalidEmail,

        /// <summary>短時間に送りすぎ（サーバー側のクールダウン）。</summary>
        TooSoon,

        /// <summary>サーバーに到達できない。</summary>
        Unreachable,
    }

    /// <summary>確認コード照合の結果。</summary>
    public enum RecoveryRedeemResult
    {
        Activated,

        /// <summary>コードが違う / 期限切れ / 試行回数超過。</summary>
        InvalidCode,

        /// <summary>そのメールアドレスでの購入が見つからない（返金済みを含む）。</summary>
        NotPurchased,

        /// <summary>サーバーに到達できない。</summary>
        Unreachable,
    }

    /// <summary>購入時のメールアドレスへ確認コードを送るよう hub へ依頼する。</summary>
    public static async Task<RecoveryRequestResult> RequestRecoveryCodeAsync(
        string email, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var res = await http.PostAsJsonAsync(
                RecoverRequestUrl,
                new RecoveryRequest { Email = email.Trim() },
                LicenseJsonContext.Default.RecoveryRequest,
                ct);

            return (int)res.StatusCode switch
            {
                200 => RecoveryRequestResult.Sent,
                400 => RecoveryRequestResult.InvalidEmail,
                429 => RecoveryRequestResult.TooSoon,
                _ => RecoveryRequestResult.Unreachable,
            };
        }
        catch (Exception ex)
        {
            Logger.Log($"確認コードの送信に失敗: {ex.Message}", LogLevel.Debug);
            return RecoveryRequestResult.Unreachable;
        }
    }

    /// <summary>確認コードを hub へ渡し、返ってきた署名キーでそのまま有効化する。</summary>
    public static async Task<RecoveryRedeemResult> RedeemRecoveryCodeAsync(
        string email, string code, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var res = await http.PostAsJsonAsync(
                RecoverRedeemUrl,
                new RecoveryRequest { Email = email.Trim(), Code = code.Trim() },
                LicenseJsonContext.Default.RecoveryRequest,
                ct);

            if ((int)res.StatusCode == 401)
            {
                return RecoveryRedeemResult.InvalidCode;
            }

            if ((int)res.StatusCode == 404)
            {
                return RecoveryRedeemResult.NotPurchased;
            }

            if (!res.IsSuccessStatusCode)
            {
                Logger.Log($"ライセンス復元が HTTP {(int)res.StatusCode}", LogLevel.Warning);
                return RecoveryRedeemResult.Unreachable;
            }

            var body = await res.Content.ReadFromJsonAsync(LicenseJsonContext.Default.RecoveryResponse, ct);
            if (body?.Key is not { Length: > 0 } key)
            {
                return RecoveryRedeemResult.Unreachable;
            }

            // 受け取ったキーも通常の認証と同じく署名検証を通す（サーバーを無条件に信頼しない）。
            return ActivateKey(key) ? RecoveryRedeemResult.Activated : RecoveryRedeemResult.Unreachable;
        }
        catch (Exception ex)
        {
            Logger.Log($"ライセンス復元に失敗: {ex.Message}", LogLevel.Debug);
            return RecoveryRedeemResult.Unreachable;
        }
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

            var verified = false;
            foreach (var spki in PublicKeysSpki)
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spki), out _);
                if (ecdsa.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256))
                {
                    verified = true;
                    break;
                }
            }

            if (!verified)
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

    /// <summary>
    /// 現在保持しているキーの購入 ID（未認証・検証不能なら null）。
    /// 失効照会の応答を適用してよいかの判定に使う。Monitor は再入可能なので、
    /// <c>Gate</c> を持ったままでも呼べる。
    /// </summary>
    private static string? CurrentPurchaseId()
    {
        lock (Gate)
        {
            return _persisted.Key is { } key && TryParseAndVerify(key, out var payload)
                ? payload.PurchaseId
                : null;
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
        var purchaseId = CurrentPurchaseId();
        if (purchaseId is null)
        {
            return false;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var check = await QueryRevocationAsync(http, purchaseId, ct);
            if (check is null)
            {
                // サーバー側の一時異常は失効と区別が付かないため猶予を消費するだけに留める
                return true;
            }
            if (check is { Valid: false })
            {
                // 返金等で失効。ローカルのライセンスを破棄して試用状態へ戻す
                var revoked = false;
                lock (Gate)
                {
                    // 照会中に別のキーが有効化された（再認証・別購入の有効化・Deactivate）場合、
                    // この応答は今のキーについてのものではない。適用すると有効なキーを消して
                    // 購入済みユーザーを試用切れへ落としてしまうので、購入 ID の一致を必ず確かめる。
                    if (CurrentPurchaseId() == purchaseId)
                    {
                        Logger.Log("ライセンスが失効しています（返金等）。ローカルのキーを無効化します", LogLevel.Warning);
                        _persisted = new PersistedLicense { MaxSeenUtc = DateTime.UtcNow.ToString("O") };
                        Save();
                        revoked = true;
                    }
                    else
                    {
                        Logger.Log("失効応答が現在のキーと一致しないため適用しません（照会中にキーが変わりました）", LogLevel.Debug);
                    }
                }

                if (!revoked)
                {
                    return true;
                }

                RecomputeState();
                NotifyStateChanged();
                return false;
            }

            var previousState = State;
            lock (Gate)
            {
                // 猶予期間の延長も、照会したキーが今も有効なキーであるときだけ行う
                if (CurrentPurchaseId() != purchaseId)
                {
                    return true;
                }

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

    /// <summary>
    /// 失効照会を優先順の URL で試行する。応答を取得できたら結果を、
    /// 全 URL が失敗（HTTP 異常・接続不可）なら null を返す（呼び出し側は猶予期間で継続）。
    /// </summary>
    private static async Task<LicenseCheckResponse?> QueryRevocationAsync(
        HttpClient http, string purchaseId, CancellationToken ct)
    {
        foreach (var url in CheckUrls)
        {
            try
            {
                using var res = await http.GetAsync($"{url}?id={Uri.EscapeDataString(purchaseId)}", ct);
                if (!res.IsSuccessStatusCode)
                {
                    Logger.Log($"ライセンス失効確認が HTTP {(int)res.StatusCode}: {url}（次の照会先へ）", LogLevel.Debug);
                    continue;
                }

                return await res.Content.ReadFromJsonAsync(LicenseJsonContext.Default.LicenseCheckResponse, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Log($"ライセンス失効確認に到達できません: {url}（{ex.Message}）", LogLevel.Debug);
            }
        }

        return null;
    }

    private static void Save()
    {
        try
        {
            // 直書きだと書き込み中の異常終了で license.json が空・途中切れになり、次回起動の
            // 読み込み失敗（= 試用状態へフォールバック）で購入済みユーザーが再認証を強いられる。
            // 設定ファイルと同じく一時ファイル + 置き換えで、失敗しても直前の内容を残す。
            AtomicFile.WriteAllText(
                StatePath, JsonSerializer.Serialize(_persisted, LicenseJsonContext.Default.PersistedLicense));
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

    /// <summary>確認コードの送信・照合に送る本文（code は送信依頼時は null）。</summary>
    internal sealed class RecoveryRequest
    {
        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("code")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Code { get; set; }
    }

    internal sealed record RecoveryResponse([property: JsonPropertyName("key")] string? Key);
}

[JsonSerializable(typeof(LicenseService.PersistedLicense))]
[JsonSerializable(typeof(LicenseService.LicensePayload))]
[JsonSerializable(typeof(LicenseService.LicenseCheckResponse))]
[JsonSerializable(typeof(LicenseService.RecoveryRequest))]
[JsonSerializable(typeof(LicenseService.RecoveryResponse))]
internal partial class LicenseJsonContext : JsonSerializerContext;
