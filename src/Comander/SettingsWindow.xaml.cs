using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Comander.Services;

namespace Comander;

public partial class SettingsWindow : Window
{
    static readonly (string Mode, string Label)[] Shells =
    {
        ("auto", "Авто — Git Bash, если найден"),
        ("bash", "Git Bash — linux-команды в Windows"),
        ("powershell", "PowerShell"),
        ("cmd", "cmd.exe"),
    };

    static readonly (string Mode, string Label)[] RemoteShells =
    {
        ("auto", "Авто — определить ОС (Linux → bash, Windows → PowerShell)"),
        ("bash", "Всегда bash (bash -l)"),
        ("sh", "Всегда sh"),
        ("powershell", "Всегда PowerShell"),
        ("cmd", "Windows: cmd (на Linux — не менять шелл)"),
        ("raw", "Сырой шелл по умолчанию — ничего не переключать"),
    };

    public string SelectedFont { get; private set; } = "Segoe UI";
    public double SelectedSize { get; private set; } = 13;
    public string SelectedShell { get; private set; } = "auto";
    public string SelectedRemoteShell { get; private set; } = "auto";
    public bool AutoStart => AutoStartBox.IsChecked == true;

    readonly SessionData _session;

    public SettingsWindow(SessionData session)
    {
        InitializeComponent();
        _session = session;

        foreach (var name in FontService.FontNames)
        {
            var rb = new RadioButton
            {
                Content = name == "Segoe UI" ? name + " (по умолчанию)" : name,
                Tag = name,
                IsChecked = session.FontFamily == name,
            };
            if (name is "Inter" or "JetBrains Mono")
                rb.FontFamily = FontService.Resolve(name); // показываем шрифт самим шрифтом
            FontList.Children.Add(rb);
        }

        foreach (var (mode, label) in Shells)
            ShellList.Children.Add(new RadioButton { Content = label, Tag = mode, IsChecked = session.ShellMode == mode });

        foreach (var (mode, label) in RemoteShells)
            RemoteShellList.Children.Add(new RadioButton { Content = label, Tag = mode, IsChecked = session.RemoteShellMode == mode });

        SizeBox.Text = session.FontSize.ToString("0.#", CultureInfo.InvariantCulture);
        AutoStartBox.IsChecked = session.AutoStart;
        RefreshProfiles();
    }

    void RefreshProfiles()
    {
        ProfilesList.Items.Clear();
        foreach (var p in _session.Profiles)
        {
            var key = string.IsNullOrEmpty(p.ProtectedPassword) ? "  (ключ)" : "";
            ProfilesList.Items.Add(string.IsNullOrEmpty(p.Alias) ? p.Name + key : $"{p.Alias}  ({p.Name}){key}");
        }
    }

    void OnDeleteProfile(object sender, RoutedEventArgs e)
    {
        var idx = ProfilesList.SelectedIndex;
        if (idx < 0 || idx >= _session.Profiles.Count) return;
        _session.Profiles.RemoveAt(idx);
        RefreshProfiles();
    }

    void OnOk(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(SizeBox.Text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var size)
            || size < 8 || size > 20)
        {
            MessageBox.Show(this, "Размер шрифта — число от 8 до 20.", "Comander",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SelectedSize = size;
        foreach (RadioButton rb in FontList.Children)
            if (rb.IsChecked == true) SelectedFont = (string)rb.Tag;
        foreach (RadioButton rb in ShellList.Children)
            if (rb.IsChecked == true) SelectedShell = (string)rb.Tag;
        foreach (RadioButton rb in RemoteShellList.Children)
            if (rb.IsChecked == true) SelectedRemoteShell = (string)rb.Tag;
        DialogResult = true;
    }
}
