using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using FindHistory.ViewModels;

namespace FindHistory;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.OpenCommand.CanExecute(null))
        {
            _viewModel.OpenCommand.Execute(null);
        }
    }

    private void OnDatabaseSettingsClick(object sender, RoutedEventArgs e)
    {
        var window = new StorageSettingsWindow(_viewModel) { Owner = this };
        window.ShowDialog();
    }

    private void OnDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        var window = new DiagnosticsWindow(_viewModel) { Owner = this };
        window.ShowDialog();
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    internal bool PrepareInactiveSelectionScreenshot()
    {
        if (ResultsGrid.Items.Count == 0)
        {
            return false;
        }

        ResultsGrid.SelectedIndex = 0;
        ResultsGrid.ScrollIntoView(ResultsGrid.SelectedItem);
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        return true;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var enabled = 1;
        var darkModeAttribute = Environment.OSVersion.Version.Build >= 18985 ? 20 : 19;
        DwmSetWindowAttribute(handle, darkModeAttribute, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute,
        ref int attributeValue, int attributeSize);
}
