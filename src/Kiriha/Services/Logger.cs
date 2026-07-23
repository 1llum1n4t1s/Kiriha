using SuperLightLogger;

namespace Kiriha.Services;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>SuperLightLogger を使用したログ出力（%LocalAppData%\Kiriha\logs、Lhamiel と同方式の簡易版）。</summary>
public static class Logger
{
    private static ILog? _logger;
    private static readonly Lock InitLock = new();

    public static void Initialize()
    {
        lock (InitLock)
        {
            if (_logger is not null)
            {
                return;
            }

            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Kiriha", "logs");
                Directory.CreateDirectory(dir);

                LogManager.Configure(builder =>
                {
                    builder.AddSuperLightFile(opt =>
                    {
                        opt.FileName = Path.Combine(dir, "Kiriha_${date:format=yyyyMMdd}.log");
                        opt.Layout = "${longdate} [${level:uppercase=true}] ${message}${onexception:inner=${newline}${exception:format=tostring}}";
                        opt.ArchiveAboveSize = 10L * 1024 * 1024;
                        opt.ArchiveFileName = Path.Combine(dir, "Kiriha_${date:format=yyyyMMdd}_{#}.log");
                        opt.ArchiveNumbering = ArchiveNumbering.Sequence;
                        opt.MaxArchiveFiles = 10;
                        opt.Encoding = System.Text.Encoding.UTF8;
                        opt.MinLevelName = "Trace";
                    });
#if DEBUG
                    builder.SetMinimumLevel("Debug");
#else
                    builder.SetMinimumLevel("Info");
#endif
                });

                _logger = LogManager.GetLogger("Kiriha");
            }
            catch (Exception ex)
            {
                // Debug.WriteLine は Release で消滅するため、配布版でも残る Trace へ出す
                System.Diagnostics.Trace.WriteLine($"ログ基盤を初期化できませんでした: {ex}");
            }
        }
    }

    public static void Log(string message, LogLevel level = LogLevel.Info)
    {
        // ログ出力自体の失敗（ディスクフル等）を呼び出し元へ伝播させない
        try
        {
            if (_logger is null)
            {
                System.Diagnostics.Trace.WriteLine($"[{level}] {message}");
                return;
            }
            switch (level)
            {
                case LogLevel.Debug:
                    _logger?.Debug(message);
                    break;
                case LogLevel.Info:
                    _logger?.Info(message);
                    break;
                case LogLevel.Warning:
                    _logger?.Warn(message);
                    break;
                case LogLevel.Error:
                    _logger?.Error(message);
                    break;
            }
        }
        catch
        {
            // ログ基盤の不調は握りつぶす（catch 内の最終文として呼ばれる契約のため）
        }
    }

    public static void LogException(string message, Exception ex)
    {
        try
        {
            if (_logger is null)
            {
                System.Diagnostics.Trace.WriteLine($"[Error] {message}: {ex}");
                return;
            }
            _logger.Error($"{message}: {ex}");
        }
        catch
        {
            // ログ基盤の不調は握りつぶす（catch 内の最終文として呼ばれる契約のため）
        }
    }
}
