using Avalonia;
using Kiriha.Services;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Velopack;

namespace Kiriha;

internal static partial class Program
{
    private const string InstanceMutexName = "Local\\Kiriha.SingleInstance";
    private const string ActivationPipeName = "Kiriha.Activate";
    private const string AppUserModelId = "velopack.Kiriha";
    private const string LegacyStartMenuFolderName = "1llum1n4t1s";
    // 起動通知の受け渡しを保護するロック。ハンドラ登録とパイプ受信の check-then-act を同一
    // クリティカルセクションに入れ、起動直後の競合で通知が消失する（lost-wakeup）のを防ぐ。
    private static readonly object ActivationGate = new();
    private static Action<string[]>? _activationHandler;
    private static string[]? _pendingActivationArgs;
    /// <summary>起動引数（フォルダーパスを渡すとそのフォルダーをタブで開く）。</summary>
    public static string[] StartupArgs { get; private set; } = [];

    [STAThread]
    public static void Main(string[] args)
    {
        TrySetCurrentProcessAppUserModelId();

        StartupArgs = args;
        Logger.Initialize();

        // グローバルな未処理例外をログに残す（多重防御）。async void ハンドラや fire-and-forget な
        // Task で発生した例外は Main の try/catch には届かないため、ここで捕捉しないとユーザーの手元での
        // クラッシュがログに残らず原因追跡が不能になる（Lhamiel と同方針）。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Logger.LogException("致命的なエラー（AppDomain）", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.LogException("未観測のタスク例外", e.Exception);
            e.SetObserved();
        };

        // Velopack のブートストラップ（インストール / 更新 / アンインストール時のフック処理）。
        // 更新適用直後の再起動などはここで処理され、通常起動時はそのまま通過する。
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => MigrateLegacyStartMenuShortcut())
            .OnAfterUpdateFastCallback(_ => MigrateLegacyStartMenuShortcut())
            .OnBeforeUninstallFastCallback(_ =>
            {
                // アンインストール後に削除済み EXE への参照を残さないよう、
                // Kiriha が HKCU に追加した Windows 統合を同期的に解除する。
                WindowsIntegrationService.SetStartupEnabled(false);
                WindowsIntegrationService.SetExplorerMenuEnabled(false);
                WindowsIntegrationService.SetDefaultFolderAppEnabled(false);
            })
            .Run();

        // v1.0.10以前のStartMenu指定で作られたショートカットを、現在のStartMenuRootへ移す。
        // 通常起動時にも補正し、旧版からの更新経路や既に更新済みの環境を取りこぼさない。
        MigrateLegacyStartMenuShortcut();

        using var instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            AllowExistingInstanceForeground();
            SignalExistingInstance(args);
            return;
        }

        _ = Task.Run(ListenForActivationAsync);

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Logger.LogException("アプリケーション起動エラー", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();

    public static void RegisterActivationHandler(Action<string[]> handler)
    {
        string[]? pending;
        lock (ActivationGate)
        {
            _activationHandler = handler;
            pending = _pendingActivationArgs;
            _pendingActivationArgs = null;
        }

        // 保留中の起動要求があればロック外で処理する（ハンドラは UI へ Post するだけなので短時間）。
        if (pending is not null)
        {
            handler(pending);
        }
    }

    private static async Task ListenForActivationAsync()
    {
        while (true)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    ActivationPipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server, leaveOpen: true);
                var count = int.TryParse(await reader.ReadLineAsync(), out var parsed)
                    ? Math.Clamp(parsed, 0, 32)
                    : 0;
                var args = new string[count];
                for (var i = 0; i < count; i++)
                {
                    args[i] = await reader.ReadLineAsync() ?? string.Empty;
                }

                Action<string[]>? handler;
                lock (ActivationGate)
                {
                    handler = _activationHandler;
                    // ハンドラ未登録なら保留に積む（登録側が同じロック下で拾う）。
                    if (handler is null)
                    {
                        _pendingActivationArgs = args;
                    }
                }

                handler?.Invoke(args);
            }
            catch (Exception ex)
            {
                Logger.LogException("既存ウィンドウの起動通知を受信できませんでした", ex);
                await Task.Delay(500);
            }
        }
    }

    private static void SignalExistingInstance(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", ActivationPipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(1500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            var safeArgs = args.Take(32).Where(arg => !arg.Contains('\n') && !arg.Contains('\r')).ToArray();
            writer.WriteLine(safeArgs.Length);
            foreach (var arg in safeArgs) writer.WriteLine(arg);
        }
        catch (Exception ex)
        {
            Logger.LogException("既存のKirihaへ起動通知を送信できませんでした", ex);
        }
    }

    private static void AllowExistingInstanceForeground()
    {
        var current = Environment.ProcessId;
        var existing = Process.GetProcessesByName("Kiriha").FirstOrDefault(p => p.Id != current);
        if (existing is not null) AllowSetForegroundWindow(existing.Id);
    }

    private static void TrySetCurrentProcessAppUserModelId()
    {
        try { _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId); }
        catch { /* シェル連携の失敗だけで起動を止めない */ }
    }

    private static void MigrateLegacyStartMenuShortcut()
    {
        try
        {
            var programsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs");
            var legacyDirectory = Path.Combine(programsDirectory, LegacyStartMenuFolderName);
            var legacyShortcut = Path.Combine(legacyDirectory, "Kiriha.lnk");
            if (!File.Exists(legacyShortcut)) return;

            var rootShortcut = Path.Combine(programsDirectory, "Kiriha.lnk");
            if (File.Exists(rootShortcut))
            {
                File.Delete(legacyShortcut);
            }
            else
            {
                File.Move(legacyShortcut, rootShortcut);
            }

            // 同じ発行者フォルダーに別アプリが残っている場合は、そのフォルダーを維持する。
            if (!Directory.EnumerateFileSystemEntries(legacyDirectory).Any())
            {
                Directory.Delete(legacyDirectory);
            }
        }
        catch
        {
            // スタートメニューの補正失敗だけでインストール・更新・通常起動を止めない。
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
