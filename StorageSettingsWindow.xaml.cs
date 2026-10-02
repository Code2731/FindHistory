using System.Windows;
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
            Description = "FindHistory 데이터베이스를 옮길 폴더를 선택하세요.",
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
                "선택한 폴더에 findhistory.db가 이미 있습니다.\n'기존 DB 사용'으로 해당 파일을 선택해 주세요.",
                "데이터베이스가 이미 있음", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            $"현재 기록을 다음 위치로 이동합니다.\n\n{targetPath}\n\n계속할까요?",
            "데이터베이스 이동", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        IsEnabled = false;
        var succeeded = await _viewModel.MoveDatabaseAsync(dialog.SelectedPath);
        IsEnabled = true;
        if (succeeded)
        {
            System.Windows.MessageBox.Show("기존 기록을 새 위치로 이동했습니다.", "이동 완료",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void OnUseDatabaseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "사용할 FindHistory 데이터베이스 선택",
            Filter = "SQLite 데이터베이스 (*.db)|*.db|모든 파일 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = _viewModel.DatabaseDirectory
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        IsEnabled = false;
        var succeeded = await _viewModel.UseDatabaseAsync(dialog.FileName);
        IsEnabled = true;
        if (!succeeded)
        {
            System.Windows.MessageBox.Show("선택한 파일을 FindHistory DB로 열 수 없습니다.", "DB 열기 실패",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnBackupClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "FindHistory 데이터베이스 백업 저장",
            Filter = "FindHistory 백업 (*.fhbackup)|*.fhbackup|SQLite 데이터베이스 (*.db)|*.db",
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
                System.Windows.MessageBox.Show($"백업을 저장했습니다.\n\n{dialog.FileName}",
                    "백업 완료", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                System.Windows.MessageBox.Show(_viewModel.StatusText, "백업 실패",
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
            Title = "복원할 FindHistory 백업 선택",
            Filter = "FindHistory 및 SQLite 백업 (*.fhbackup;*.db)|*.fhbackup;*.db|모든 파일 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = _viewModel.DatabaseDirectory
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            "선택한 백업의 기록으로 현재 데이터베이스를 교체합니다.\n" +
            "현재 DB는 복원 전 안전 사본으로 자동 보관됩니다.\n\n계속할까요?",
            "데이터베이스 복원", MessageBoxButton.YesNo, MessageBoxImage.Warning);
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
                System.Windows.MessageBox.Show(
                    $"백업 복원을 완료했습니다.\n\n복원 전 DB 사본:\n{safetyPath}",
                    "복원 완료", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                System.Windows.MessageBox.Show(_viewModel.StatusText, "복원 실패",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e) => _viewModel.OpenDatabaseFolder();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
