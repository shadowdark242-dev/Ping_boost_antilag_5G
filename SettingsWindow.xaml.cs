using System.Windows;
using System.Windows.Controls;
using PingBoost.Services;

namespace PingBoost;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly GameProfile _currentDraft;

    public SettingsWindow(AppSettings settings, GameProfile currentDraft)
    {
        InitializeComponent();
        _settings = CopySettings(settings);
        _currentDraft = currentDraft;
        LanguageComboBox.SelectedIndex = settings.Language == "en" ? 1 : 0;
        StartupCheckBox.IsChecked = settings.StartWithWindows;
        ProfilesListBox.ItemsSource = settings.Profiles;
        ProfilesListBox.DisplayMemberPath = nameof(GameProfile.Name);
        if (!string.IsNullOrWhiteSpace(settings.SelectedProfile))
            ProfilesListBox.SelectedItem = settings.Profiles.FirstOrDefault(profile => profile.Name == settings.SelectedProfile);
        if (ProfilesListBox.SelectedItem is null && settings.Profiles.Count > 0)
            ProfilesListBox.SelectedIndex = 0;
        LocalizationService.Apply(this, settings.Language);
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var language = LanguageComboBox.SelectedItem is ComboBoxItem { Tag: string value } ? value : "ru";
        LocalizationService.Apply(this, language);
    }

    public bool Saved { get; private set; }
    public AppSettings? ResultSettings => Saved ? _settings : null;

    private static AppSettings CopySettings(AppSettings value) => new()
    {
        Language = value.Language,
        StartWithWindows = value.StartWithWindows,
        SelectedProfile = value.SelectedProfile,
        Profiles = value.Profiles.Select(CopyProfile).ToList()
    };

    private void ProfilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.RemovedItems.OfType<GameProfile>().FirstOrDefault() is { } previous)
            _ = UpdateProfileFromFields(previous);
        if (ProfilesListBox.SelectedItem is GameProfile profile)
            ShowProfile(profile);
    }

    private void ShowProfile(GameProfile profile)
    {
        ProfileNameTextBox.Text = profile.Name;
        ProcessNameTextBox.Text = profile.ProcessName;
        MonitorTargetsTextBox.Text = profile.MonitorTargets;
        IdleTargetTextBox.Text = profile.IdleTarget;
        IdleSecondsTextBox.Text = profile.IdleSeconds.ToString();
        KeepAliveUrlTextBox.Text = profile.KeepAliveUrl;
        KeepAliveSecondsTextBox.Text = profile.KeepAliveSeconds.ToString();
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var profile = CopyProfile(_currentDraft);
        profile.Name = $"Game profile {_settings.Profiles.Count + 1}";
        _settings.Profiles.Add(profile);
        ProfilesListBox.Items.Refresh();
        ProfilesListBox.SelectedItem = profile;
        ProfileNameTextBox.Focus();
        ProfileNameTextBox.SelectAll();
    }

    private static GameProfile CopyProfile(GameProfile value) => new()
    {
        Name = value.Name,
        ProcessName = value.ProcessName,
        MonitorTargets = value.MonitorTargets,
        IdleTarget = value.IdleTarget,
        IdleSeconds = value.IdleSeconds,
        KeepAliveUrl = value.KeepAliveUrl,
        KeepAliveSeconds = value.KeepAliveSeconds
    };

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesListBox.SelectedItem is not GameProfile profile)
            return;
        _settings.Profiles.Remove(profile);
        if (_settings.SelectedProfile == profile.Name)
            _settings.SelectedProfile = string.Empty;
        ProfilesListBox.Items.Refresh();
        ProfilesListBox.SelectedIndex = _settings.Profiles.Count > 0 ? 0 : -1;
        if (ProfilesListBox.SelectedItem is null)
            ShowProfile(new GameProfile());
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesListBox.SelectedItem is GameProfile profile)
        {
            if (!UpdateProfileFromFields(profile))
                return;
            _settings.SelectedProfile = profile.Name;
            ProfilesListBox.Items.Refresh();
        }

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

    private bool UpdateProfileFromFields(GameProfile profile)
    {
        if (!int.TryParse(IdleSecondsTextBox.Text, out var idleSeconds) || idleSeconds is not (5 or 15 or 30 or 60)
            || !int.TryParse(KeepAliveSecondsTextBox.Text, out var keepAliveSeconds) || keepAliveSeconds is not (2 or 5 or 10 or 20 or 30 or 60))
        {
            MessageBox.Show(this, "Выберите поддерживаемые значения: пауза 5/15/30/60 секунд, keepalive 2/5/10/20/30/60 секунд.", "Некорректные параметры", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        profile.Name = string.IsNullOrWhiteSpace(ProfileNameTextBox.Text) ? "Game profile" : ProfileNameTextBox.Text.Trim();
        profile.ProcessName = ProcessNameTextBox.Text.Trim();
        profile.MonitorTargets = MonitorTargetsTextBox.Text.Trim();
        profile.IdleTarget = IdleTargetTextBox.Text.Trim();
        profile.IdleSeconds = idleSeconds;
        profile.KeepAliveUrl = KeepAliveUrlTextBox.Text.Trim();
        profile.KeepAliveSeconds = keepAliveSeconds;
        return true;
    }
}
