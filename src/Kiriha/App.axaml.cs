using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Kiriha.ViewModels;
using Kiriha.Views;

namespace Kiriha;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // UI スレッドの async void ハンドラで漏れた例外は既定ではプロセスを落とす。ログに残した上で
        // 続行扱いにし、単発の操作失敗でアプリ全体が落ちないようにする（AppDomain 側と合わせて多重防御）。
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Services.Logger.LogException("UI スレッドの未処理例外", e.Exception);
            e.Handled = true;
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel();
            Services.ThemeService.Initialize(this, viewModel.OptUseAcrylicBackground);
            var mainWindow = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = mainWindow;
            Program.RegisterActivationHandler(args => Dispatcher.UIThread.Post(() =>
            {
                viewModel.OpenShellPaths(args);
                mainWindow.RestoreFromTray();
            }));

            SetupTrayIcon(mainWindow, viewModel, desktop);

            // Opened はトレイ格納からの Show() 復元でも再発火するため、起動時1回だけ行うべき
            // 更新チェック・トレイ格納開始は購読解除して多重実行を防ぐ（Lhamiel と同方式）。
            EventHandler? onFirstOpened = null;
            onFirstOpened = (_, _) =>
            {
                mainWindow.Opened -= onFirstOpened;

                // 「起動時にタスクトレイに格納する」ON: 開いた直後に隠す（Discord 相当）。
                var startingMinimizedToTray = viewModel.OptStartMinimizedToTray && !Program.StartupArgs.Any(Directory.Exists);
                if (startingMinimizedToTray)
                {
                    mainWindow.Hide();
                }

                // 更新ダイアログは mainWindow をオーナーとして表示するため、直前で非表示にしていると
                // 「非表示のオーナーではウィンドウを表示できない」で例外になる。トレイ格納起動時は
                // 自動チェック自体を今回はスキップする（手動チェック、または通常起動時に改めてチェックされる）。
                if (viewModel.Settings.CheckUpdatesOnStartup && !startingMinimizedToTray)
                {
                    Services.UpdateService.Check4Update(mainWindow, viewModel.Settings, manually: false);
                }
            };
            mainWindow.Opened += onFirstOpened;
        }
        else
        {
            Services.ThemeService.Initialize(this, acrylicEnabled: false);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>タスクトレイアイコンを構築する。表示条件は「最小化時にトレイへ格納」「起動時にトレイへ格納」の
    /// いずれかが ON（アプリ起動中は常時表示、Discord 相当）。</summary>
    private void SetupTrayIcon(MainWindow mainWindow, MainWindowViewModel viewModel, IClassicDesktopStyleApplicationLifetime desktop)
    {
        using var iconStream = AssetLoader.Open(new Uri("avares://Kiriha/icon/app_icon.png"));
        var trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            ToolTipText = "Kiriha",
            IsVisible = false,
        };

        var openItem = new NativeMenuItem("Kiriha を開く");
        openItem.Click += (_, _) => mainWindow.RestoreFromTray();
        var exitItem = new NativeMenuItem("終了");
        exitItem.Click += (_, _) => desktop.Shutdown();

        var menu = new NativeMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(exitItem);
        trayIcon.Menu = menu;
        trayIcon.Clicked += (_, _) => mainWindow.RestoreFromTray();

        var icons = new TrayIcons { trayIcon };
        TrayIcon.SetIcons(this, icons);

        void UpdateVisibility() => trayIcon.IsVisible = viewModel.OptMinimizeToTray || viewModel.OptStartMinimizedToTray;
        UpdateVisibility();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainWindowViewModel.OptMinimizeToTray)
                or nameof(MainWindowViewModel.OptStartMinimizedToTray))
            {
                UpdateVisibility();
            }
        };
    }
}
