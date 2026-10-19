using System.Windows;
using System.Windows.Controls;
using PingBoost.Services;

namespace PingBoost;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = new AppSettings
        {
            Language = settings.Language,
            StartWithWindows = settings.StartWithWindows,
            GameProcessName = settings.GameProcessName
        };
        LanguageComboBox.SelectedIndex = settings.Language == "en" ? 1 : 0;
        StartupCheckBox.IsChecked = settings.StartWithWindows;
        LocalizationService.Apply(this, settings.Language);
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var language = LanguageComboBox.SelectedItem is ComboBoxItem { Tag: string value } ? value : "ru";
        LocalizationService.Apply(this, language);
    }

    public bool Saved { get; private set; }
    public AppSettings? ResultSettings => Saved ? _settings : null;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.Language = LanguageComboBox.SelectedItem is ComboBoxItem { Tag: string language } ? language : "ru";
        _settings.StartWithWindows = StartupCheckBox.IsChecked == true;
        try
        {
            WindowsStartupService.SetEnabled(_settings.StartWithWindows);
            Saved = true;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            MessageBox.Show(this, $"Не удалось изменить автозапуск: {ex.Message}", "Ошибка автозапуска", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
