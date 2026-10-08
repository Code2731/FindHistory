using System.Windows;
using FindHistory.Localization;
using FindHistory.Models;
using FindHistory.ViewModels;
using Forms = System.Windows.Forms;

namespace FindHistory;

public partial class ProjectSettingsWindow : Window
{
    private readonly MainViewModel _viewModel;
    private Guid? _editingId;

    public ProjectSettingsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void OnProjectSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProjectList.SelectedItem is not ProjectDefinition project) return;
        _editingId = project.Id;
        ProjectName.Text = project.Name;
        ProjectFolder.Text = project.Folder;
    }

    private void OnNewProject(object sender, RoutedEventArgs e)
    {
        _editingId = null;
        ProjectList.SelectedItem = null;
        ProjectName.Clear();
        ProjectFolder.Clear();
        ProjectName.Focus();
    }

    private void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = T("프로젝트의 기준 폴더를 선택하세요."), UseDescriptionForTitle = true,
            SelectedPath = ProjectFolder.Text
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) ProjectFolder.Text = dialog.SelectedPath;
    }

    private void OnSaveProject(object sender, RoutedEventArgs e)
    {
        var id = _editingId ?? Guid.NewGuid();
        if (!_viewModel.SaveProject(new ProjectDefinition(id, ProjectName.Text, ProjectFolder.Text)))
        {
            ShowError();
            return;
        }
        _editingId = id;
        ProjectList.SelectedItem = _viewModel.Projects.Single(project => project.Id == id);
    }

    private void OnDeleteProject(object sender, RoutedEventArgs e)
    {
        if (_editingId is not Guid id) return;
        if (System.Windows.MessageBox.Show(this, T("프로젝트 설정을 삭제할까요? 파일과 기록은 유지됩니다."),
                T("프로젝트 관리"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!_viewModel.RemoveProject(id)) { ShowError(); return; }
        OnNewProject(sender, e);
    }

    private void ShowError() => System.Windows.MessageBox.Show(this, _viewModel.StatusText,
        T("프로젝트 관리"), MessageBoxButton.OK, MessageBoxImage.Warning);

    private static string T(string text) => LocalizationManager.Instance.Translate(text);
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
