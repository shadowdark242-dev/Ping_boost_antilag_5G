using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PingBoost.Services;

namespace PingBoost;

public partial class MainWindow
{
    private readonly TuningService _tuning = new();
    private readonly UdpFlowMonitor _udpMonitor = new();
    private readonly LocalLossMonitor _localLoss = new();
    private readonly GameProcessPriorityService _priority = new();
    private readonly Dictionary<string, CheckBox> _tweakChecks = [];
    private DispatcherTimer? _udpTimer;
    private DispatcherTimer? _priorityTimer;
    private CancellationTokenSource? _mtuCancellation;
    private int? _probedMtu;
    private bool _gameModeActive;

    public ObservableCollection<UdpFlowRow> UdpRows { get; } = [];

    private AdapterInfo? SelectedAdapter => TuningAdapterComboBox.SelectedItem as AdapterInfo;

    private string GameProcessName => System.IO.Path.GetFileNameWithoutExtension(GameProcessComboBox.Text.Trim());

    private void InitializeTuning()
    {
        foreach (var tweak in TuningService.Tweaks)
        {
            var check = new CheckBox { Content = tweak.Title, ToolTip = tweak.Description, VerticalAlignment = VerticalAlignment.Center };
            _tweakChecks[tweak.Id] = check;
            check.Margin = new Thickness(0, 6, 0, 0);
            TweaksPanel.Children.Add(check);
            TweaksPanel.Children.Add(new TextBlock
            {
                Text = tweak.Description,
                Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20, 1, 0, 0)
            });
        }

        AdminBanner.Visibility = PowerShellRunner.IsAdministrator() ? Visibility.Collapsed : Visibility.Visible;
        UpdateRestoreButtons();
        _ = LoadTuningAdaptersAsync();
    }

    private void ShutdownTuning()
    {
        _udpTimer?.Stop();
        _priorityTimer?.Stop();
        _mtuCancellation?.Cancel();
        _udpMonitor.Dispose();
        _priority.Restore();
        if (_gameModeActive)
        {
            // The window is closing: restore the power plan and services without blocking forever.
            try { Task.Run(() => _tuning.ExitGameModeAsync()).Wait(TimeSpan.FromSeconds(20)); }
            catch (AggregateException) { }
        }
    }

    private void UpdateRestoreButtons()
    {
        RestorePreviousButton.IsEnabled = _tuning.HasBackup;
    }

    private void AppendTuningLog(IEnumerable<string> lines)
    {
        // The log accumulates so the result of "apply" is not lost when the state is shown afterwards.
        TuningLogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}]{Environment.NewLine}{string.Join(Environment.NewLine, lines)}{Environment.NewLine}{Environment.NewLine}");
        TuningLogTextBox.ScrollToEnd();
    }

    private bool Confirm(string message, string title) =>
        MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private bool RequireAdministratorRights()
    {
        if (PowerShellRunner.IsAdministrator())
            return true;
        AdminBanner.Visibility = Visibility.Visible;
        MessageBox.Show(this, "Для этого действия нужны права администратора. Нажмите «Перезапустить от администратора» на вкладке «Оптимизация».",
            "PingBoost", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private void SetTuningBusy(bool busy)
    {
        ApplyTweaksButton.IsEnabled = !busy;
        RestorePreviousButton.IsEnabled = !busy && _tuning.HasBackup;
        ResetDefaultsButton.IsEnabled = !busy;
        ProbeMtuButton.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void RestartAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (Win32Exception)
        {
            // The user declined the UAC prompt; stay in the current window.
        }
    }

    // ------------------------------------------------------------ adapter & system tweaks

    private async Task LoadTuningAdaptersAsync()
    {
        try
        {
            var adapters = await _tuning.GetAdaptersAsync();
            TuningAdapterComboBox.ItemsSource = adapters;
            TuningAdapterComboBox.SelectedIndex = adapters.Count > 0 ? 0 : -1;
            if (adapters.Count == 0)
                AppendTuningLog(["Активные сетевые адаптеры не найдены."]);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or System.Text.Json.JsonException)
        {
            AppendTuningLog([$"Не удалось получить список адаптеров: {ex.Message}"]);
        }
    }

    private void RefreshTuningAdapters_Click(object sender, RoutedEventArgs e) => _ = LoadTuningAdaptersAsync();

    private async void ApplyTweaks_Click(object sender, RoutedEventArgs e)
    {
        var ids = _tweakChecks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToArray();
        if (ids.Length == 0)
        {
            AppendTuningLog(["Отметьте хотя бы один пункт."]);
            return;
        }
        if (!RequireAdministratorRights())
            return;
        if (!Confirm("Будут изменены выбранные настройки Windows и сетевого адаптера. Исходные значения сохранятся, "
                     + "вернуть их можно кнопками «Откат настроек». Некоторые параметры перезапускают адаптер, и соединение пропадёт на несколько секунд.\n\nПродолжить?",
                "Применить настройки"))
            return;

        SetTuningBusy(true);
        try
        {
            AppendTuningLog(await _tuning.ApplyAsync(ids, SelectedAdapter));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            AppendTuningLog([$"Ошибка: {ex.Message}"]);
        }
        finally
        {
            SetTuningBusy(false);
        }
    }

    private async void ShowTuningState_Click(object sender, RoutedEventArgs e)
    {
        SetTuningBusy(true);
        try
        {
            AppendTuningLog([await _tuning.DescribeAsync(SelectedAdapter)]);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or System.Text.Json.JsonException)
        {
            AppendTuningLog([$"Не удалось прочитать состояние: {ex.Message}"]);
        }
        finally
        {
            SetTuningBusy(false);
        }
    }

    // ------------------------------------------------------------ MTU

    private async void ProbeMtu_Click(object sender, RoutedEventArgs e)
    {
        var host = MtuHostTextBox.Text.Trim();
        if (!IsPlausibleHost(host))
        {
            MtuResultText.Text = "Введите корректный IP-адрес или DNS-имя.";
            return;
        }

        _probedMtu = null;
        ApplyMtuButton.IsEnabled = false;
        ProbeMtuButton.IsEnabled = false;
        var cancellation = new CancellationTokenSource();
        _mtuCancellation = cancellation;
        try
        {
            var progress = new Progress<string>(message => MtuResultText.Text = message);
            var result = await MtuProbeService.ProbeAsync(host, progress, cancellation.Token);
            var text = result.Message;
            if (result.Mtu is { } found && SelectedAdapter is { } adapter)
            {
                var current = await _tuning.GetMtuAsync(adapter);
                text += $" Текущий MTU адаптера «{adapter.Name}»: {current?.ToString() ?? "неизвестен"}.";
                if (current is { } now && found < now)
                {
                    _probedMtu = found;
                    ApplyMtuButton.IsEnabled = true;
                    text += " Можно уменьшить MTU до найденного значения.";
                }
            }
            MtuResultText.Text = text;
        }
        catch (OperationCanceledException)
        {
            MtuResultText.Text = "Проверка остановлена.";
        }
        finally
        {
            cancellation.Dispose();
            _mtuCancellation = null;
            ProbeMtuButton.IsEnabled = true;
        }
    }

    private async void ApplyMtu_Click(object sender, RoutedEventArgs e)
    {
        if (_probedMtu is not { } mtu || SelectedAdapter is not { } adapter || !RequireAdministratorRights())
            return;
        if (!Confirm($"Установить MTU {mtu} для адаптера «{adapter.Name}»? Предыдущее значение сохранится для отката.", "Изменить MTU"))
            return;

        SetTuningBusy(true);
        try
        {
            await _tuning.SetMtuAsync(adapter, mtu);
            MtuResultText.Text = $"MTU {mtu} установлен для «{adapter.Name}».";
            ApplyMtuButton.IsEnabled = false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            MtuResultText.Text = $"Не удалось изменить MTU: {ex.Message}";
        }
        finally
        {
            SetTuningBusy(false);
        }
    }

    // ------------------------------------------------------------ QoS

    private async void EnableQos_Click(object sender, RoutedEventArgs e)
    {
        if (GameProcessName.Length == 0)
        {
            QosStatusText.Text = "Укажите процесс игры в блоке «Игра» (вкладка «Тесты»), например cs2.";
            return;
        }
        if (!RequireAdministratorRights())
            return;
        var dscp = DscpComboBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var value) ? value : 46;
        if (!Confirm($"Создать политику QoS: UDP-трафик {GameProcessName}.exe будет помечаться DSCP {dscp}? Политику можно удалить кнопкой «Убрать».", "QoS"))
            return;

        SetTuningBusy(true);
        try
        {
            await _tuning.EnableQosAsync(GameProcessName, dscp);
            QosStatusText.Text = string.Join(Environment.NewLine, await _tuning.ListQosPoliciesAsync());
            UpdateRestoreButtons();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            QosStatusText.Text = $"Не удалось создать политику: {ex.Message}";
        }
        finally
        {
            SetTuningBusy(false);
        }
    }

    private async void RemoveQos_Click(object sender, RoutedEventArgs e)
    {
        if (GameProcessName.Length == 0)
        {
            QosStatusText.Text = "Укажите процесс игры в блоке «Игра».";
            return;
        }
        if (!RequireAdministratorRights())
            return;

        SetTuningBusy(true);
        try
        {
            await _tuning.RemoveQosAsync(GameProcessName);
            var policies = await _tuning.ListQosPoliciesAsync();
            QosStatusText.Text = policies.Count == 0 ? "Политика удалена." : string.Join(Environment.NewLine, policies);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            QosStatusText.Text = $"Не удалось удалить политику: {ex.Message}";
        }
        finally
        {
            SetTuningBusy(false);
        }
    }

    // ------------------------------------------------------------ upload throttle

    private async void EnableThrottle_Click(object sender, RoutedEventArgs e)
    {
        var names = ThrottleProcessesTextBox.Text
            .Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length == 0)
        {
            ThrottleStatusText.Text = "Укажите хотя бы одну программу.";
            return;
        }
        if (GameProcessName.Length > 0 && names.Any(name => System.IO.Path.GetFileNameWithoutExtension(name).Equals(GameProcessName, StringComparison.OrdinalIgnoreCase)))
        {
            ThrottleStatusText.Text = $"Уберите из списка процесс игры ({GameProcessName}): ограничивать нужно только фоновые программы.";
            return;
        }
        if (!RequireAdministratorRights())
            return;
        var rate = ThrottleRateComboBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var value) ? value : 10;
        if (!Confirm($"Ограничить исходящую скорость программ ({string.Join(", ", names)}) до {rate} Мбит/с каждая? Ограничение можно снять кнопкой «Снять ограничения».", "Ограничение отдачи"))
            return;

        SetTuningBusy(true);
        try
        {
            var log = await _tuning.EnableThrottleAsync(names, rate);
            ThrottleStatusText.Text = string.Join(Environment.NewLine, log);
            UpdateRestoreButtons();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            ThrottleStatusText.Text = $"Не удалось применить ограничение: {ex.Message}";
        }
        finally
        {
            SetTuningBusy(false);
        }
    }

    private async void EnableGlobalThrottle_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdministratorRights())
            return;
        var rate = GlobalThrottleRateComboBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var value) ? value : 40;
        if (!Confirm($"Ограничить исходящую скорость всего компьютера до {rate} Мбит/с? Скорость отдачи файлов и видео упадёт до этого значения, игра на потолок не упирается. Потолок можно снять кнопкой «Снять ограничения».", "Общий потолок отдачи"))
            return;

        SetTuningBusy(true);
        try
        {
            ThrottleStatusText.Text = await _tuning.EnableGlobalThrottleAsync(rate);
            UpdateRestoreButtons();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            ThrottleStatusText.Text = $"Не удалось включить потолок: {ex.Message}";
        }
        finally
        {
            SetTuningBusy(false);
        }
    }

    private async void RemoveThrottle_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdministratorRights())
            return;
        SetTuningBusy(true);
        try
        {
            await _tuning.RemoveThrottlesAsync();
            ThrottleStatusText.Text = "Ограничения сняты.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            ThrottleStatusText.Text = $"Не удалось снять ограничения: {ex.Message}";
        }
        finally
        {
            SetTuningBusy(false);
        }
    }

    // ------------------------------------------------------------ game mode

    private async void GameModeStart_Click(object sender, RoutedEventArgs e)
    {
        var raisePriority = GameModePriorityCheckBox.IsChecked == true;
        var powerPlan = GameModePowerCheckBox.IsChecked == true;
        var services = GameModeServicesCheckBox.IsChecked == true;
        if (!raisePriority && !powerPlan && !services)
        {
            GameModeStatusText.Text = "Отметьте хотя бы один пункт.";
            return;
        }
        if (raisePriority && GameProcessName.Length == 0)
        {
            GameModeStatusText.Text = "Для повышения приоритета укажите процесс игры в блоке «Игра» (вкладка «Тесты»).";
            return;
        }
        if ((powerPlan || services) && !RequireAdministratorRights())
            return;

        GameModeStartButton.IsEnabled = false;
        try
        {
            var parts = new List<string>();
            if (powerPlan || services)
                parts.Add(await _tuning.EnterGameModeAsync(powerPlan, services));
            if (raisePriority)
            {
                _priorityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _priorityTimer.Tick += (_, _) => _priority.Apply(GameProcessName);
                _priorityTimer.Start();
                var changed = _priority.Apply(GameProcessName);
                parts.Add(changed > 0
                    ? $"приоритет «Высокий» у {changed} процесс(ов) {GameProcessName}"
                    : $"процесс {GameProcessName} пока не запущен; приоритет будет повышен, когда он появится");
            }
            _gameModeActive = true;
            GameModeStopButton.IsEnabled = true;
            GameModeStatusText.Text = "Включён: " + string.Join("; ", parts) + ".";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            GameModeStatusText.Text = $"Не удалось включить игровой режим: {ex.Message}";
            GameModeStartButton.IsEnabled = true;
        }
    }

    private async void GameModeStop_Click(object sender, RoutedEventArgs e)
    {
        GameModeStopButton.IsEnabled = false;
        _priorityTimer?.Stop();
        _priorityTimer = null;
        _priority.Restore();
        try
        {
            var result = PowerShellRunner.IsAdministrator() ? await _tuning.ExitGameModeAsync() : "права администратора не получены, схема питания не менялась";
            GameModeStatusText.Text = $"Выключен: {result}.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            GameModeStatusText.Text = $"Не удалось полностью выключить режим: {ex.Message}";
        }
        finally
        {
            _gameModeActive = false;
            GameModeStartButton.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------ restore

    private async void RestorePrevious_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdministratorRights())
            return;
        if (!Confirm("Вернуть все настройки, изменённые PingBoost, к значениям, которые были до первого изменения? Адаптеры будут перезапущены.", "Вернуть как было"))
            return;
        await RunRestoreAsync(() => _tuning.RestorePreviousAsync());
    }

    private async void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdministratorRights())
            return;
        if (!Confirm("Выставить стандартные значения Windows для всех параметров, которыми управляет PingBoost (адаптеры, TCP, MMCSS, QoS, MTU, схема питания)? Адаптеры будут перезапущены.", "Стандартные настройки Windows"))
            return;
        await RunRestoreAsync(() => _tuning.ResetToWindowsDefaultsAsync());
    }

    private async Task RunRestoreAsync(Func<Task<IReadOnlyList<string>>> operation)
    {
        SetTuningBusy(true);
        try
        {
            _priorityTimer?.Stop();
            _priority.Restore();
            AppendTuningLog(await operation());
            _gameModeActive = false;
            GameModeStartButton.IsEnabled = true;
            GameModeStopButton.IsEnabled = false;
            GameModeStatusText.Text = "Выключен.";
            QosStatusText.Text = "Политика не создана.";
            ThrottleStatusText.Text = "Ограничений нет.";
            ApplyMtuButton.IsEnabled = false;
            foreach (var check in _tweakChecks.Values)
                check.IsChecked = false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or Win32Exception
                                       or System.Security.SecurityException or System.IO.IOException)
        {
            AppendTuningLog([$"Ошибка: {ex.Message}"]);
        }
        finally
        {
            SetTuningBusy(false);
            UpdateRestoreButtons();
        }
    }

    // ------------------------------------------------------------ UDP monitor

    private void UdpStart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _udpMonitor.Start(GameProcessName);
        }
        catch (UnauthorizedAccessException ex)
        {
            AdminBanner.Visibility = Visibility.Visible;
            UdpStatusText.Text = ex.Message + " Откройте вкладку «Оптимизация» и перезапустите приложение от администратора.";
            return;
        }
        catch (Exception ex)
        {
            // ETW session creation can fail in several ways (session limit, policy); report the reason instead of crashing.
            UdpStatusText.Text = $"Не удалось запустить наблюдение: {ex.Message}";
            return;
        }

        UdpRows.Clear();
        UdpEventsTextBox.Clear();
        UdpLocalText.Text = "Счётчики запущены, роста пока нет.";
        _localLoss.Start();
        UdpStartButton.IsEnabled = false;
        UdpStopButton.IsEnabled = true;
        UdpStatusText.Text = GameProcessName.Length == 0
            ? "Наблюдение идёт за всеми процессами."
            : $"Наблюдение идёт за процессом {GameProcessName}. Запустите игру или зайдите в матч.";
        _udpTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _udpTimer.Tick += (_, _) =>
        {
            UdpRows.Clear();
            foreach (var row in _udpMonitor.GetRows())
                UdpRows.Add(row);
            UpdateLocalLoss();
        };
        _udpTimer.Start();
    }

    private void UpdateLocalLoss()
    {
        var report = _localLoss.Poll();
        if (report.NewEvents is { } events)
        {
            UdpEventsTextBox.AppendText($"{DateTime.Now:HH:mm:ss}  {events}{Environment.NewLine}");
            UdpEventsTextBox.ScrollToEnd();
        }
        var verdict = report.Total == 0
            ? "Вывод: локальных потерь нет. Лаги с паузами приёма идут извне (сеть, радиоканал)."
            : "Вывод: ПК теряет пакеты. Проверьте CPU, драйвер и питание адаптера.";
        UdpLocalText.Text = string.Join(Environment.NewLine,
            $"UDP, отброшено системой: {report.UdpDiscarded} · с ошибками: {report.UdpErrors}",
            $"Адаптер, входящие: потеряно {report.NicInDiscarded} · ошибок {report.NicInErrors}",
            $"Адаптер, исходящие: потеряно {report.NicOutDiscarded} · ошибок {report.NicOutErrors}",
            verdict);
    }

    private void UdpStop_Click(object sender, RoutedEventArgs e)
    {
        _udpTimer?.Stop();
        _udpTimer = null;
        _udpMonitor.Stop();
        UdpStartButton.IsEnabled = true;
        UdpStopButton.IsEnabled = false;
        UdpStatusText.Text = "Остановлено.";
    }
}
