using System.Windows;
using System.Windows.Input;
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
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.OpenCommand.CanExecute(null))
        {
            _viewModel.OpenCommand.Execute(null);
        }
    }
}
