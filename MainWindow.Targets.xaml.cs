using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using PingBoost.Services;

namespace PingBoost;

public partial class MainWindow
{
    private const int MaximumTargets = 8;

    private static readonly (string Name, string Address)[] PresetTargets =
    [
        ("Cloudflare", "1.1.1.1"),
        ("Google", "8.8.8.8"),
        ("Cloudflare (запасной)", "1.0.0.1"),
        ("Google (запасной)", "8.8.4.4"),
        ("Quad9", "9.9.9.9"),
        ("OpenDNS", "208.67.222.222"),
        ("Яндекс DNS", "77.88.8.8"),
        ("AdGuard", "94.140.14.14"),
        ("CleanBrowsing", "185.228.168.9")
    ];

    private static readonly string[] DefaultTargets = ["1.1.1.1", "8.8.8.8"];

    private void InitializeTargets()
    {
        var selected = _settings.TestTargets.Count > 0 ? _settings.TestTargets : [.. DefaultTargets];
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var gateway = DetectGateway();
        if (gateway is not null)
            AddTargetCheckBox($"Шлюз (роутер или модем) — {gateway}", gateway, selected, known);
        foreach (var (name, address) in PresetTargets)
            AddTargetCheckBox($"{name} — {address}", address, selected, known);
        foreach (var address in _settings.CustomTargets)
            AddTargetCheckBox($"Свой — {address}", address, selected, known);
        UpdateTargetsSummary();
    }

    private void AddTargetCheckBox(string title, string address, IEnumerable<string> selected, HashSet<string> known)
    {
        if (!known.Add(address))
            return;
        var check = new CheckBox
        {
            Content = title,
            Tag = address,
            IsChecked = selected.Contains(address, StringComparer.OrdinalIgnoreCase),
            Width = 300,
            Margin = new Thickness(0, 3, 12, 3)
        };
        check.Click += (_, _) =>
        {
            UpdateTargetsSummary();
            SaveTargets();
        };
        TargetsPanel.Children.Add(check);
    }

    private IEnumerable<CheckBox> TargetCheckBoxes => TargetsPanel.Children.OfType<CheckBox>();

    private IReadOnlyList<string> GetSelectedTargets() =>
        TargetCheckBoxes.Where(check => check.IsChecked == true).Select(check => (string)check.Tag).ToArray();

    private void UpdateTargetsSummary()
    {
        var targets = GetSelectedTargets();
        TargetsSummaryText.Text = targets.Count == 0
            ? "Выбрано: ничего. Откройте список и отметьте адреса."
            : $"Выбрано ({targets.Count}): {string.Join(", ", targets)}";
        if (targets.Count > MaximumTargets)
            TargetsSummaryText.Text += $"\nОтмечено больше {MaximumTargets}: для проверки нужно оставить не более {MaximumTargets}.";
    }

    private void SaveTargets()
    {
        _settings.TestTargets = [.. GetSelectedTargets()];
        try { _settingsService.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The selection simply is not remembered for the next start.
        }
    }

    private void AddCustomTarget_Click(object sender, RoutedEventArgs e)
    {
        var address = CustomTargetTextBox.Text.Trim();
        if (!IsPlausibleHost(address))
        {
            CustomTargetStatusText.Text = "Введите корректный IP-адрес или DNS-имя.";
            return;
        }
        if (TargetCheckBoxes.Any(check => string.Equals((string)check.Tag, address, StringComparison.OrdinalIgnoreCase)))
        {
            CustomTargetStatusText.Text = "Этот адрес уже есть в списке.";
            return;
        }

        var known = new HashSet<string>(TargetCheckBoxes.Select(check => (string)check.Tag), StringComparer.OrdinalIgnoreCase);
        AddTargetCheckBox($"Свой — {address}", address, [address], known);
        _settings.CustomTargets.Add(address);
        CustomTargetTextBox.Clear();
        CustomTargetStatusText.Text = $"Добавлено и отмечено: {address}.";
        UpdateTargetsSummary();
        SaveTargets();
    }

    private static string? DetectGateway()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up
                                  && adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(adapter => adapter.GetIPProperties().GatewayAddresses)
                .Select(entry => entry.Address)
                .FirstOrDefault(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                           && !address.Equals(IPAddress.Any))
                ?.ToString();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}
