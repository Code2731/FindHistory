using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;
using FindHistory.Services;
using FindHistory.ViewModels;

namespace FindHistory;

public partial class App : System.Windows.Application
{
    private RecentItemsMonitor? _monitor;
    private MainWindow? _window;
    private Forms.NotifyIcon? _trayIcon;
    private bool _isExiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var settings = new AppSettingsService();
            var database = new RecentDatabase(settings.DatabasePath);
            await database.InitializeAsync();

            _monitor = new RecentItemsMonitor(database, new ShortcutResolver());
            var viewModel = new MainViewModel(database, _monitor, new AutoStartService(), settings);
            _window = new MainWindow(viewModel);
            _window.Closing += OnWindowClosing;

            CreateTrayIcon();
            await viewModel.InitializeAsync();

            var settingsScreenshotIndex = Array.FindIndex(e.Args,
                arg => arg.Equals("--screenshot-settings", StringComparison.OrdinalIgnoreCase));
            var screenshotIndex = Array.FindIndex(e.Args,
                arg => arg.Equals("--screenshot", StringComparison.OrdinalIgnoreCase));
            if (settingsScreenshotIndex >= 0 && settingsScreenshotIndex + 1 < e.Args.Length)
            {
                var settingsWindow = new StorageSettingsWindow(viewModel);
                settingsWindow.Show();
                settingsWindow.UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                SaveScreenshot(settingsWindow, e.Args[settingsScreenshotIndex + 1]);
                settingsWindow.Close();
                ExitApplication();
            }
            else if (screenshotIndex >= 0 && screenshotIndex + 1 < e.Args.Length)
            {
                _window.Show();
                _window.UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                SaveScreenshot(_window, e.Args[screenshotIndex + 1]);
                ExitApplication();
            }
            else if (!e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase))
            {
                ShowWindow();
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"FindHistory를 시작하지 못했습니다.\n\n{ex.Message}", "FindHistory",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ExitApplication();
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
        menu.Items.Add("종료", null, (_, _) => ExitApplication());

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "FindHistory - 최근 항목 기록 중",
            Icon = System.Drawing.SystemIcons.Information,
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => ShowWindow();
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

    private void ExitApplication()
    {
        _isExiting = true;
        _monitor?.Dispose();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _window?.Close();
        Shutdown();
    }
}
