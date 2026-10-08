using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;
using FindHistory.Services;
using FindHistory.Localization;
using FindHistory.ViewModels;

namespace FindHistory;

public partial class App : System.Windows.Application
{
    private readonly AppLogService _log = new();
    private RecentDatabase? _database;
    private RecentItemsMonitor? _monitor;
    private AutoBackupService? _autoBackup;
    private FileExistenceMonitor? _existenceMonitor;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _trayOpenItem;
    private Forms.ToolStripMenuItem? _trayExitItem;
    private System.Drawing.Icon? _trayApplicationIcon;
    private Mutex? _singleInstanceMutex;
    private bool _isExiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterUnhandledExceptionLogging();
        _log.Information($"FindHistory {GetType().Assembly.GetName().Version} starting.");

        var screenshotsEnabled =
            Environment.GetEnvironmentVariable("FINDHISTORY_ENABLE_SCREENSHOTS") == "1";
        var isScreenshotRun = screenshotsEnabled &&
                              (HasOutputArgument(e.Args, "--screenshot") ||
                               HasOutputArgument(e.Args, "--screenshot-settings") ||
                               HasOutputArgument(e.Args, "--screenshot-projects") ||
                               HasOutputArgument(e.Args, "--screenshot-diagnostics") ||
                               HasOutputArgument(e.Args, "--screenshot-inactive-selection"));
        var mutexName = isScreenshotRun
            ? $"FindHistory.Isolated.{Environment.ProcessId}"
            : "FindHistory.SingleInstance";

        _singleInstanceMutex = new Mutex(initiallyOwned: true, mutexName,
            out var isFirstInstance);
        if (!isFirstInstance)
        {
            _log.Information("A second application instance was rejected.");
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            await ExitApplicationAsync();
            return;
        }

        try
        {
            var screenshotSettingsIndex = isScreenshotRun
                ? Array.FindIndex(e.Args, arg => arg.Equals("--settings", StringComparison.OrdinalIgnoreCase))
                : -1;
            var settings = new AppSettingsService(
                screenshotSettingsIndex >= 0 && screenshotSettingsIndex + 1 < e.Args.Length
                    ? e.Args[screenshotSettingsIndex + 1] : null);
            LocalizationManager.Instance.SetLanguage(settings.Language);
            var databaseIndex = Array.FindIndex(e.Args,
                arg => arg.Equals("--database", StringComparison.OrdinalIgnoreCase));
            var databasePath = databaseIndex >= 0 && databaseIndex + 1 < e.Args.Length
                ? e.Args[databaseIndex + 1]
                : settings.DatabasePath;
            _database = new RecentDatabase(databasePath);
            await _database.InitializeAsync();

            _monitor = new RecentItemsMonitor(_database, new ShortcutResolver(), log: _log, settings: settings);
            _viewModel = new MainViewModel(
                _database, _monitor, new AutoStartService(), settings, _log);
            _viewModel.PropertyChanged += OnRecordingPropertyChanged;
            _window = new MainWindow(_viewModel);
            _window.Closing += OnWindowClosing;

            CreateTrayIcon();
            var settingsScreenshotIndex = screenshotsEnabled
                ? Array.FindIndex(e.Args,
                    arg => arg.Equals("--screenshot-settings", StringComparison.OrdinalIgnoreCase))
                : -1;
            var diagnosticsScreenshotIndex = screenshotsEnabled
                ? Array.FindIndex(e.Args,
                    arg => arg.Equals("--screenshot-diagnostics", StringComparison.OrdinalIgnoreCase))
                : -1;
            var screenshotIndex = screenshotsEnabled
                ? Array.FindIndex(e.Args,
                    arg => arg.Equals("--screenshot", StringComparison.OrdinalIgnoreCase))
                : -1;
            var inactiveSelectionScreenshotIndex = screenshotsEnabled
                ? Array.FindIndex(e.Args,
                    arg => arg.Equals("--screenshot-inactive-selection",
                        StringComparison.OrdinalIgnoreCase))
                : -1;
            var isBackground = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
            if (!isBackground && !isScreenshotRun)
            {
                ShowWindow();
            }

            await _viewModel.InitializeAsync();

            if (!isScreenshotRun)
            {
                _autoBackup = new AutoBackupService(_database, settings, _log);
                _autoBackup.Start();
                _existenceMonitor = new FileExistenceMonitor(_database, _log);
                _existenceMonitor.HistoryChanged += OnExistenceChanged;
                _existenceMonitor.Start();
            }

            var projectsScreenshotIndex = isScreenshotRun
                ? Array.FindIndex(e.Args, arg => arg.Equals("--screenshot-projects", StringComparison.OrdinalIgnoreCase))
                : -1;
            if (projectsScreenshotIndex >= 0 && projectsScreenshotIndex + 1 < e.Args.Length)
            {
                var projectsWindow = new ProjectSettingsWindow(_viewModel);
                projectsWindow.Show();
                projectsWindow.UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                SaveScreenshot(projectsWindow, e.Args[projectsScreenshotIndex + 1]);
                projectsWindow.Close();
                await ExitApplicationAsync();
            }
            else if (settingsScreenshotIndex >= 0 && settingsScreenshotIndex + 1 < e.Args.Length)
            {
                var settingsWindow = new StorageSettingsWindow(_viewModel);
                settingsWindow.Show();
                if (e.Args.Contains("--scroll-settings-bottom", StringComparer.OrdinalIgnoreCase))
                    settingsWindow.PrepareBottomScreenshot();
                settingsWindow.UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                SaveScreenshot(settingsWindow, e.Args[settingsScreenshotIndex + 1]);
                settingsWindow.Close();
                await ExitApplicationAsync();
            }
            else if (diagnosticsScreenshotIndex >= 0 &&
                     diagnosticsScreenshotIndex + 1 < e.Args.Length)
            {
                var diagnosticsWindow = new DiagnosticsWindow(_viewModel);
                diagnosticsWindow.Show();
                await diagnosticsWindow.RefreshReportAsync();
                diagnosticsWindow.UpdateLayout();
                await Dispatcher.InvokeAsync(
                    () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                SaveScreenshot(diagnosticsWindow, e.Args[diagnosticsScreenshotIndex + 1]);
                diagnosticsWindow.Close();
                await ExitApplicationAsync();
            }
            else if (inactiveSelectionScreenshotIndex >= 0 &&
                     inactiveSelectionScreenshotIndex + 1 < e.Args.Length)
            {
                _window.Show();
                _window.PrepareInactiveSelectionScreenshot();
                _window.UpdateLayout();
                await Dispatcher.InvokeAsync(
                    () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                SaveScreenshot(_window, e.Args[inactiveSelectionScreenshotIndex + 1]);
                await ExitApplicationAsync();
            }
            else if (screenshotIndex >= 0 && screenshotIndex + 1 < e.Args.Length)
            {
                _window.Show();
                _window.UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                SaveScreenshot(_window, e.Args[screenshotIndex + 1]);
                await ExitApplicationAsync();
            }
        }
        catch (Exception ex)
        {
            _log.Error("Application startup failed.", ex);
            System.Windows.MessageBox.Show(LocalizationManager.Instance.Translate(
                    $"FindHistory를 시작하지 못했습니다.\n\n{ex.Message}"), "FindHistory",
                MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitApplicationAsync();
        }
    }

    private void OnExistenceChanged(object? sender, EventArgs e) => _viewModel?.NotifyExistenceChanged();

    private void RegisterUnhandledExceptionLogging()
    {
        // Command failures are handled at their boundary. Other unhandled UI failures remain fatal:
        // continuing with unknown database or UI state could damage history. Log before shutdown.
        DispatcherUnhandledException += (_, args) =>
            _log.Error("Unhandled UI exception.", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                _log.Error("Unhandled application exception.", exception);
            }
            else
            {
                _log.Warning($"Unhandled non-exception object: {args.ExceptionObject}");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log.Error("Unobserved background task exception.", args.Exception);
            args.SetObserved();
        };
    }

    private static void SaveScreenshot(Window window, string outputPath)
    {
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    private static bool HasOutputArgument(IReadOnlyList<string> arguments, string option)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals(option, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(arguments[index + 1]))
            {
                return true;
            }
        }
        return false;
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        _trayOpenItem = new Forms.ToolStripMenuItem();
        _trayOpenItem.Click += (_, _) => ShowWindow();
        menu.Items.Add(_trayOpenItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        _trayExitItem = new Forms.ToolStripMenuItem();
        _trayExitItem.Click += (_, _) => _ = ExitApplicationAsync();
        menu.Items.Add(_trayExitItem);
        LocalizationManager.Instance.PropertyChanged += OnLanguageChanged;
        UpdateTrayLanguage();

        _trayApplicationIcon = LoadTrayIcon();
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "FindHistory - " + LocalizationManager.Instance.Translate(
                _monitor?.IsRecordingPaused == true ? "기록 일시정지 중" : "최근 항목 기록 중"),
            Icon = _trayApplicationIcon,
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => ShowWindow();
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocalizationManager.Language))
        {
            UpdateTrayLanguage();
        }
    }

    private void OnRecordingPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsRecordingPaused)) UpdateTrayLanguage();
    }

    private void UpdateTrayLanguage()
    {
        _trayOpenItem?.Text = LocalizationManager.Instance.Translate("FindHistory 열기");
        _trayExitItem?.Text = LocalizationManager.Instance.Translate("종료");
        if (_trayIcon is not null)
        {
            _trayIcon.Text = "FindHistory - " + LocalizationManager.Instance.Translate(
                _monitor?.IsRecordingPaused == true ? "기록 일시정지 중" : "최근 항목 기록 중");
        }
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var resource = GetResourceStream(new Uri("pack://application:,,,/Assets/findhistory.ico"));
            if (resource is not null)
            {
                using var icon = new System.Drawing.Icon(resource.Stream);
                return (System.Drawing.Icon)icon.Clone();
            }
        }
        catch (Exception)
        {
            // 아이콘 리소스가 손상되어도 앱 기록 기능은 계속 시작한다.
        }

        return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }
        _window.Activate();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        e.Cancel = true;
        _window?.Hide();
    }

    private async Task ExitApplicationAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _log.Information("FindHistory shutdown requested.");
        try
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnRecordingPropertyChanged;
            _viewModel?.Dispose();
            _viewModel = null;
            if (_monitor is not null)
            {
                await _monitor.DisposeAsync();
                _monitor = null;
            }
        }
        catch (Exception ex)
        {
            _log.Error("An error occurred while stopping background services.", ex);
        }
        finally
        {
            if (_existenceMonitor is not null)
            {
                _existenceMonitor.HistoryChanged -= OnExistenceChanged;
                try { await _existenceMonitor.DisposeAsync(); }
                catch (Exception ex) { _log.Error("Stopping file existence refresh failed.", ex); }
                _existenceMonitor = null;
            }
            if (_autoBackup is not null)
            {
                try { await _autoBackup.DisposeAsync(); }
                catch (Exception ex) { _log.Error("Stopping automatic backup failed.", ex); }
                _autoBackup = null;
            }
            LocalizationManager.Instance.PropertyChanged -= OnLanguageChanged;
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            _trayApplicationIcon?.Dispose();
            _trayApplicationIcon = null;
            _window?.Close();
            var database = _database;
            _database = null;
            if (database is not null)
            {
                try
                {
                    await database.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _log.Error("An error occurred while closing the database.", ex);
                }
            }
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
            _log.Information("FindHistory shutdown completed.");
            Shutdown();
        }
    }
}
