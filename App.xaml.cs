using System.Windows;
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
            var database = new RecentDatabase(AppPaths.DatabasePath);
            await database.InitializeAsync();

            _monitor = new RecentItemsMonitor(database, new ShortcutResolver());
            var viewModel = new MainViewModel(database, _monitor, new AutoStartService());
            _window = new MainWindow(viewModel);
            _window.Closing += OnWindowClosing;

            CreateTrayIcon();
            await viewModel.InitializeAsync();

            if (!e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase))
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
