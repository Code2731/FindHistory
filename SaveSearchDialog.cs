using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FindHistory.Localization;
using FindHistory.Services;

namespace FindHistory;

internal sealed class SaveSearchDialog : Window
{
    private readonly System.Windows.Controls.TextBox _nameBox;
    private readonly TextBlock _validationMessage;
    private readonly IReadOnlyCollection<string> _existingNames;

    public string SearchName => _nameBox.Text.Trim();

    public SaveSearchDialog(IReadOnlyCollection<string> existingNames)
    {
        _existingNames = existingNames;
        Title = LocalizationManager.Instance.Translate("검색 저장");
        Width = 390;
        Height = 220;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        _nameBox = new System.Windows.Controls.TextBox
        {
            Margin = new Thickness(0, 10, 0, 2),
            MinHeight = 40,
            MaxLength = AppSettingsService.MaxSavedSearchNameLength,
            FontSize = 15,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _validationMessage = new TextBlock
        {
            Margin = new Thickness(0, 2, 0, 10),
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(248, 113, 113)),
            MinHeight = 18
        };
        var content = new StackPanel { Margin = new Thickness(20) };
        content.Children.Add(new TextBlock
        {
            Text = LocalizationManager.Instance.Translate(
                "현재 검색 조건에 이름을 붙여 저장합니다. 이름은 최대 64자이며 중복할 수 없습니다."),
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(185, 185, 196))
        });
        content.Children.Add(_nameBox);
        content.Children.Add(_validationMessage);
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        var cancel = new System.Windows.Controls.Button
            { Content = LocalizationManager.Instance.Translate("취소"), IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var save = new System.Windows.Controls.Button { Content = LocalizationManager.Instance.Translate("저장"), IsDefault = true, MinWidth = 82 };
        save.Click += (_, _) =>
        {
            if (SearchName.Length == 0)
            {
                _validationMessage.Text = LocalizationManager.Instance.Translate("이름을 입력하세요.");
                _nameBox.Focus();
                return;
            }
            if (_existingNames.Any(name => string.Equals(name?.Trim(), SearchName,
                    StringComparison.OrdinalIgnoreCase)))
            {
                _validationMessage.Text = LocalizationManager.Instance.Translate(
                    "같은 이름의 검색이 이미 있습니다.");
                _nameBox.Focus();
                _nameBox.SelectAll();
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
