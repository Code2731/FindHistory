using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FindHistory;

internal sealed class SaveSearchDialog : Window
{
    private readonly System.Windows.Controls.TextBox _nameBox;

    public string SearchName => _nameBox.Text.Trim();

    public SaveSearchDialog()
    {
        Title = "검색 저장";
        Width = 390;
        Height = 190;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _nameBox = new System.Windows.Controls.TextBox
        {
            Margin = new Thickness(0, 10, 0, 14),
            MinHeight = 40,
            FontSize = 15,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var content = new StackPanel { Margin = new Thickness(20) };
        content.Children.Add(new TextBlock
        {
            Text = "현재 검색 조건에 이름을 붙여 저장합니다.",
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(185, 185, 196))
        });
        content.Children.Add(_nameBox);
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        var cancel = new System.Windows.Controls.Button
            { Content = "취소", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var save = new System.Windows.Controls.Button { Content = "저장", IsDefault = true, MinWidth = 82 };
        save.Click += (_, _) =>
        {
            if (SearchName.Length == 0)
            {
                _nameBox.Focus();
                return;
            }

            DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        content.Children.Add(buttons);
        Content = content;
        Loaded += (_, _) =>
        {
            _nameBox.Focus();
            Keyboard.Focus(_nameBox);
        };
    }
}
