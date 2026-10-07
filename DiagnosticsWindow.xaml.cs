using System.Windows;
using FindHistory.Localization;
using FindHistory.ViewModels;

namespace FindHistory;

public partial class DiagnosticsWindow : Window
{
    private readonly MainViewModel _viewModel;
    private Task? _refreshTask;

    public DiagnosticsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await RefreshReportAsync();

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshReportAsync();

    internal Task RefreshReportAsync()
    {
        if (_refreshTask is { IsCompleted: false })
        {
            return _refreshTask;
        }

        _refreshTask = RefreshReportCoreAsync();
        return _refreshTask;
    }

    private async Task RefreshReportCoreAsync()
    {
        RefreshButton.IsEnabled = false;
        try
        {
            ReportTextBox.Text = await _viewModel.BuildDiagnosticsReportAsync();
            ReportTextBox.ScrollToHome();
        }
        catch (Exception ex)
        {
            ReportTextBox.Text = LocalizationManager.Instance.Translate(
                $"진단 정보를 불러오지 못했습니다.\n\n{ex.Message}");
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(ReportTextBox.Text))
            {
                System.Windows.Clipboard.SetText(ReportTextBox.Text);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                LocalizationManager.Instance.Translate(
                    $"진단 정보를 클립보드에 복사하지 못했습니다.\n\n{ex.Message}"),
                LocalizationManager.Instance.Translate("복사 실패"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenLogFolderClick(object sender, RoutedEventArgs e) => _viewModel.OpenLogFolder();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
