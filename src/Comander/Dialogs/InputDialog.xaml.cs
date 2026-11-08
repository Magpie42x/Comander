using System.Windows;

namespace Comander.Dialogs;

public partial class InputDialog : Window
{
    readonly bool _isPassword;

    public string Value => (_isPassword ? PasswordBox.Password : ValueBox.Text).Trim();

    public InputDialog(string title, string prompt, string initial = "", bool isPassword = false)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        _isPassword = isPassword;
        if (isPassword)
        {
            ValueBox.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Visible;
        }
        else
        {
            ValueBox.Text = initial;
        }
        Loaded += (_, _) =>
        {
            if (isPassword) PasswordBox.Focus();
            else { ValueBox.Focus(); ValueBox.SelectAll(); }
        };
    }

    void OnOk(object sender, RoutedEventArgs e)
    {
        if (!_isPassword && string.IsNullOrWhiteSpace(Value)) return;
        DialogResult = true;
    }
}
