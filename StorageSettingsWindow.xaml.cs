using System.Windows;
using FindHistory.Localization;
using FindHistory.ViewModels;
using Forms = System.Windows.Forms;

namespace FindHistory;

public partial class StorageSettingsWindow : Window
{
    private readonly MainViewModel _viewModel;

    public StorageSettingsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private async void OnMoveDatabaseClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = T("FindHistory 데이터베이스를 옮길 폴더를 선택하세요."),
            UseDescriptionForTitle = true,
            InitialDirectory = _viewModel.DatabaseDirectory,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        var targetPath = Path.Combine(dialog.SelectedPath, "findhistory.db");
        if (File.Exists(targetPath))
        {
            System.Windows.MessageBox.Show(
                T("선택한 폴더에 findhistory.db가 이미 있습니다.\n'기존 DB 사용'으로 해당 파일을 선택해 주세요."),
                T("데이터베이스가 이미 있음"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var movePrompt = LocalizationManager.Instance.Format(
            "현재 기록을 다음 위치로 이동합니다.\n\n{0}\n\n계속할까요?",
            "Move current history to this location:\n\n{0}\n\nContinue?", targetPath);
        var answer = System.Windows.MessageBox.Show(
            movePrompt, T("데이터베이스 이동"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        IsEnabled = false;
        var succeeded = false;
        try
        {
            succeeded = await _viewModel.MoveDatabaseAsync(dialog.SelectedPath);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(T($"데이터베이스 이동 중 오류가 발생했습니다.\n\n{ex.Message}"),
                T("DB 이동 실패"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsEnabled = true;
        }
        if (succeeded)
        {
            System.Windows.MessageBox.Show(T("기존 기록을 새 위치로 이동했습니다."), T("이동 완료"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void OnUseDatabaseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = T("사용할 FindHistory 데이터베이스 선택"),
            Filter = T("SQLite 데이터베이스 (*.db)|*.db|모든 파일 (*.*)|*.*"),
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = _viewModel.DatabaseDirectory
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        IsEnabled = false;
        var succeeded = false;
        string? operationError = null;
        try
        {
            succeeded = await _viewModel.UseDatabaseAsync(dialog.FileName);
        }
        catch (Exception ex)
        {
            operationError = ex.Message;
        }
        finally
        {
            IsEnabled = true;
        }
        if (operationError is not null)
        {
            System.Windows.MessageBox.Show(T($"데이터베이스 전환 중 오류가 발생했습니다.\n\n{operationError}"),
                T("DB 열기 실패"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else if (!succeeded)
        {
            System.Windows.MessageBox.Show(T("선택한 파일을 FindHistory DB로 열 수 없습니다."), T("DB 열기 실패"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnBackupClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = T("FindHistory 데이터베이스 백업 저장"),
            Filter = T("FindHistory 백업 (*.fhbackup)|*.fhbackup|SQLite 데이터베이스 (*.db)|*.db"),
            DefaultExt = ".fhbackup",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = _viewModel.DatabaseDirectory,
            FileName = $"FindHistory-backup-{DateTime.Now:yyyyMMdd-HHmmss}.fhbackup"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        IsEnabled = false;
        try
        {
            if (await _viewModel.BackupDatabaseAsync(dialog.FileName))
            {
                System.Windows.MessageBox.Show(LocalizationManager.Instance.Format(
                        "백업을 저장했습니다.\n\n{0}", "Backup saved.\n\n{0}", dialog.FileName),
                    T("백업 완료"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                System.Windows.MessageBox.Show(_viewModel.StatusText, T("백업 실패"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = T("복원할 FindHistory 백업 선택"),
            Filter = T("FindHistory 및 SQLite 백업 (*.fhbackup;*.db)|*.fhbackup;*.db|모든 파일 (*.*)|*.*"),
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = _viewModel.DatabaseDirectory
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            T("선택한 백업의 기록으로 현재 데이터베이스를 교체합니다.\n" +
              "현재 DB는 복원 전 안전 사본으로 자동 보관됩니다.\n\n계속할까요?"),
            T("데이터베이스 복원"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        IsEnabled = false;
        try
        {
            var safetyPath = await _viewModel.RestoreDatabaseAsync(dialog.FileName);
            if (safetyPath is not null)
            {
                System.Windows.MessageBox.Show(LocalizationManager.Instance.Format(
                        "백업 복원을 완료했습니다.\n\n복원 전 DB 사본:\n{0}",
                        "Backup restored.\n\nPrevious database copy:\n{0}", safetyPath),
                    T("복원 완료"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                System.Windows.MessageBox.Show(_viewModel.StatusText, T("복원 실패"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void OnExportCsvClick(object sender, RoutedEventArgs e) => await ExportAsync("csv");

    private async void OnExportJsonClick(object sender, RoutedEventArgs e) => await ExportAsync("json");

    private async void OnExportResultsCsvClick(object sender, RoutedEventArgs e) =>
        await ExportAsync("csv", currentResultsOnly: true);

    private async void OnExportResultsJsonClick(object sender, RoutedEventArgs e) =>
        await ExportAsync("json", currentResultsOnly: true);

    private async Task ExportAsync(string format, bool currentResultsOnly = false)
    {
        if (currentResultsOnly && _viewModel.Items.Count == 0)
        {
            System.Windows.MessageBox.Show(T("현재 표시된 검색 결과가 없습니다."), T("내보내기 실패"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var isCsv = format.Equals("csv", StringComparison.OrdinalIgnoreCase);
        var extension = isCsv ? ".csv" : ".json";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = T(currentResultsOnly
                ? isCsv ? "현재 표시된 검색 결과를 CSV로 내보내기" : "현재 표시된 검색 결과를 JSON으로 내보내기"
                : isCsv ? "전체 기록을 CSV로 내보내기" : "전체 기록을 JSON으로 내보내기"),
            Filter = T(currentResultsOnly
                ? isCsv ? "현재 결과 CSV 파일 (*.csv)|*.csv" : "현재 결과 JSON 파일 (*.json)|*.json"
                : isCsv ? "CSV 파일 (*.csv)|*.csv" : "JSON 파일 (*.json)|*.json"),
            DefaultExt = extension,
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = _viewModel.DatabaseDirectory,
            FileName = $"FindHistory-{(currentResultsOnly ? "search-results" : "export")}-{DateTime.Now:yyyyMMdd-HHmmss}{extension}"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        IsEnabled = false;
        try
        {
            var succeeded = currentResultsOnly
                ? await _viewModel.ExportCurrentResultsAsync(dialog.FileName, format)
                : await _viewModel.ExportDatabaseAsync(dialog.FileName, format);
            if (succeeded)
            {
                var completionMessage = currentResultsOnly
                    ? LocalizationManager.Instance.Format(
                        "현재 표시된 검색 결과를 내보냈습니다.\n\n{0}",
                        "Current search results exported.\n\n{0}", dialog.FileName)
                    : LocalizationManager.Instance.Format(
                        "전체 기록을 내보냈습니다.\n\n{0}", "History exported.\n\n{0}", dialog.FileName);
                System.Windows.MessageBox.Show(completionMessage,
                    T("내보내기 완료"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                System.Windows.MessageBox.Show(_viewModel.StatusText, T("내보내기 실패"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e) => _viewModel.OpenDatabaseFolder();

    private void OnOpenAutoBackupFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_viewModel.AutoBackupDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", $"\"{_viewModel.AutoBackupDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, T("데이터 저장소"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string T(string text) => LocalizationManager.Instance.Translate(text);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
