using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;
using FindHistory.Services;
using FindHistory.ViewModels;

namespace FindHistory;

public partial class App : System.Windows.Application
{
    private RecentDatabase? _database;
    private RecentItemsMonitor? _monitor;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private Forms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _trayApplicationIcon;
    private Mutex? _singleInstanceMutex;
    private bool _isExiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, "FindHistory.SingleInstance",
            out var isFirstInstance);
        if (!isFirstInstance)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            await ExitApplicationAsync();
            return;
        }

        try
        {
            var settings = new AppSettingsService();
            var databaseIndex = Array.FindIndex(e.Args,
                arg => arg.Equals("--database", StringComparison.OrdinalIgnoreCase));
            var databasePath = databaseIndex >= 0 && databaseIndex + 1 < e.Args.Length
                ? e.Args[databaseIndex + 1]
                : settings.DatabasePath;
            _database = new RecentDatabase(databasePath);
            await _database.InitializeAsync();

            _monitor = new RecentItemsMonitor(_database, new ShortcutResolver());
            _viewModel = new MainViewModel(_database, _monitor, new AutoStartService(), settings);
            _window = new MainWindow(_viewModel);
            _window.Closing += OnWindowClosing;

            CreateTrayIcon();
            var screenshotsEnabled =
                Environment.GetEnvironmentVariable("FINDHISTORY_ENABLE_SCREENSHOTS") == "1";
            var settingsScreenshotIndex = screenshotsEnabled
                ? Array.FindIndex(e.Args,
                    arg => arg.Equals("--screenshot-settings", StringComparison.OrdinalIgnoreCase))
                : -1;
            var screenshotIndex = screenshotsEnabled
                ? Array.FindIndex(e.Args,
                    arg => arg.Equals("--screenshot", StringComparison.OrdinalIgnoreCase))
                : -1;
            var isBackground = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
            var isScreenshotRun = settingsScreenshotIndex >= 0 || screenshotIndex >= 0;
            if (!isBackground && !isScreenshotRun)
            {
                ShowWindow();
            }

            await _viewModel.InitializeAsync();

            if (settingsScreenshotIndex >= 0 && settingsScreenshotIndex + 1 < e.Args.Length)
            {
                var settingsWindow = new StorageSettingsWindow(_viewModel);
                settingsWindow.Show();
                settingsWindow.UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                SaveScreenshot(settingsWindow, e.Args[settingsScreenshotIndex + 1]);
                settingsWindow.Close();
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
            System.Windows.MessageBox.Show($"FindHistory를 시작하지 못했습니다.\n\n{ex.Message}", "FindHistory",
                MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitApplicationAsync();
        }
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

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("FindHistory 열기", null, (_, _) => ShowWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => _ = ExitApplicationAsync());

        _trayApplicationIcon = LoadTrayIcon();
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "FindHistory - 최근 항목 기록 중",
            Icon = _trayApplicationIcon,
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => ShowWindow();
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
        _viewModel?.Dispose();
        _viewModel = null;
        if (_monitor is not null)
        {
            await _monitor.DisposeAsync();
            _monitor = null;
        }
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _trayApplicationIcon?.Dispose();
        _trayApplicationIcon = null;
        _window?.Close();
        _database?.Dispose();
        _database = null;
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        Shutdown();
    }
}
