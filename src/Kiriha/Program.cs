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
    private static Action<string[]>? _activationHandler;
    private static int _pendingActivation;
    private static string[]? _pendingActivationArgs;
    /// <summary>起動引数（フォルダーパスを渡すとそのフォルダーをタブで開く）。</summary>
    public static string[] StartupArgs { get; private set; } = [];

    [STAThread]
    public static void Main(string[] args)
    {
        StartupArgs = args;
        Logger.Initialize();

        // Velopack のブートストラップ（インストール / 更新 / アンインストール時のフック処理）。
        // 更新適用直後の再起動などはここで処理され、通常起動時はそのまま通過する。
        VelopackApp.Build().Run();

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
        _activationHandler = handler;
        if (Interlocked.Exchange(ref _pendingActivation, 0) != 0)
        {
            handler(Interlocked.Exchange(ref _pendingActivationArgs, null) ?? []);
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

                var handler = _activationHandler;
                if (handler is null)
                {
                    Interlocked.Exchange(ref _pendingActivationArgs, args);
                    Interlocked.Exchange(ref _pendingActivation, 1);
                }
                else
                {
                    handler(args);
                }
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

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
