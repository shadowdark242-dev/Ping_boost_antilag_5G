using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using PingBoost.Services;

namespace PingBoost;

public partial class MainWindow : Window
{
    private const int HistoryLimitPerTarget = 240;
    private const int StatisticsWindow = 30;
    private const int IdleTestRounds = 5;
    private const string WaveformBufferbloatUrl = "https://www.waveform.com/tools/bufferbloat";

    private readonly AppSettingsService _settingsService = new();
    private AppSettings _settings;
    private readonly NetworkMonitor _monitor = new();
    private readonly RouteTraceService _routeTrace = new();
    private readonly List<PingSample> _samples = [];
    private readonly Dictionary<string, BaselineSnapshot> _baseline = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient KeepAliveClient = new() { Timeout = TimeSpan.FromSeconds(8) };
    private CancellationTokenSource? _monitoringCancellation;
    private CancellationTokenSource? _idleTestCancellation;
    private CancellationTokenSource? _keepAliveCancellation;
    private bool _loadingProfiles = true;

    public ObservableCollection<TargetRow> TargetRows { get; } = [];
    public ObservableCollection<RouteHopRow> RouteRows { get; } = [];
    public ObservableCollection<IdleLatencyRow> IdleRows { get; } = [];

    public MainWindow()
    {
        _settings = _settingsService.Load();
        InitializeComponent();
        if (Environment.ProcessPath is { } exePath && File.Exists(exePath))
            Title += $" · сборка {File.GetLastWriteTime(exePath):yyyy-MM-dd HH:mm}";
        DataContext = this;
        Loaded += (_, _) =>
        {
            RefreshAdapters();
            LoadProfiles();
            InitializeTuning();
            LocalizationService.Apply(this, _settings.Language);
        };
        ChartCanvas.SizeChanged += (_, _) => DrawChart();
        Closed += (_, _) =>
        {
            _monitoringCancellation?.Cancel();
            _idleTestCancellation?.Cancel();
            _keepAliveCancellation?.Cancel();
            ShutdownTuning();
        };
    }

    private void RefreshAdapters_Click(object sender, RoutedEventArgs e) => RefreshAdapters();

    private GameProfile CreateProfileSnapshot() => new()
    {
        Name = "Current configuration",
        ProcessName = GameProcessComboBox.Text.Trim(),
        MonitorTargets = TargetsTextBox.Text,
        IdleTarget = IdleTargetTextBox.Text.Trim(),
        IdleSeconds = GetSelectedSeconds(IdleIntervalComboBox, 15),
        KeepAliveUrl = KeepAliveUrlTextBox.Text.Trim(),
        KeepAliveSeconds = GetSelectedSeconds(KeepAliveIntervalComboBox, 10)
    };

    private void LoadProfiles()
    {
        _loadingProfiles = true;
        GameProfileComboBox.ItemsSource = null;
        GameProfileComboBox.ItemsSource = _settings.Profiles;
        GameProfileComboBox.SelectedItem = _settings.Profiles.FirstOrDefault(profile => profile.Name == _settings.SelectedProfile);
        _loadingProfiles = false;
        if (GameProfileComboBox.SelectedItem is GameProfile profile)
            ApplyProfile(profile);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings, CreateProfileSnapshot()) { Owner = this };
        if (dialog.ShowDialog() != true || !dialog.Saved)
            return;
        _settings = dialog.ResultSettings!;
        try
        {
            _settingsService.Save(_settings);
            LoadProfiles();
            LocalizationService.Apply(this, _settings.Language);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Не удалось сохранить настройки: {ex.Message}", "PingBoost", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void GameProfile_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingProfiles || GameProfileComboBox.SelectedItem is not GameProfile profile)
            return;
        ApplyProfile(profile);
        _settings.SelectedProfile = profile.Name;
        try { _settingsService.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            GameStatusText.Text = $"Не удалось сохранить выбранный профиль: {ex.Message}";
        }
    }

    private void ApplyProfile(GameProfile profile)
    {
        TargetsTextBox.Text = profile.MonitorTargets;
        IdleTargetTextBox.Text = profile.IdleTarget;
        IdleIntervalComboBox.SelectedItem = FindComboItemByTag(IdleIntervalComboBox, profile.IdleSeconds);
        KeepAliveUrlTextBox.Text = profile.KeepAliveUrl;
        KeepAliveIntervalComboBox.SelectedItem = FindComboItemByTag(KeepAliveIntervalComboBox, profile.KeepAliveSeconds);
        GameProcessComboBox.Text = profile.ProcessName;
    }

    private static object? FindComboItemByTag(System.Windows.Controls.ComboBox combo, int seconds) =>
        combo.Items.Cast<object>().FirstOrDefault(item => item is System.Windows.Controls.ComboBoxItem entry
                                                          && int.TryParse(entry.Tag?.ToString(), out var value)
                                                          && value == seconds);

    private void RefreshProcesses_Click(object sender, RoutedEventArgs e) => RefreshGameProcesses();

    private void RefreshGameProcesses()
    {
        var selected = GameProcessComboBox.Text;
        try
        {
            var names = GameProcessService.GetUdpEndpoints().Select(endpoint => endpoint.ProcessName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
            GameProcessComboBox.ItemsSource = names;
            GameProcessComboBox.Text = selected;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            GameStatusText.Text = $"Не удалось получить список игровых процессов с UDP endpoint: {ex.Message}";
        }
    }

    private void RefreshAdapters()
    {
        try
        {
            var adapters = _monitor.GetActiveAdapters();
            AdapterText.Text = adapters.Count == 0
                ? "Активные сетевые адаптеры не найдены."
                : string.Join(Environment.NewLine, adapters);
        }
        catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException)
        {
            AdapterText.Text = $"Не удалось прочитать список адаптеров: {ex.Message}";
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var targets = ParseTargets(TargetsTextBox.Text);
        if (targets.Count == 0 || targets.Any(target => !IsPlausibleHost(target)))
        {
            ComparisonText.Text = "Введите корректные IP-адреса или DNS-имена.";
            return;
        }

        if (targets.Count > 8)
        {
            ComparisonText.Text = "Для удобства и снижения сетевой нагрузки можно проверять не более 8 целей одновременно.";
            return;
        }

        _samples.Clear();
        TargetRows.Clear();
        ChartTargetComboBox.ItemsSource = targets;
        ChartTargetComboBox.SelectedIndex = 0;
        ComparisonText.Text = "Мониторинг запущен. Для сравнения снимите исходный замер кнопкой «Сохранить ДО».";
        RecommendationsList.ItemsSource = RecommendationService.GetInitialRecommendations();
        UpdateMetricCards(null);

        TargetsTextBox.IsEnabled = false;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;

        _monitoringCancellation = new CancellationTokenSource();
        try
        {
            await MonitorTargetsAsync(targets, _monitoringCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Пользовательская остановка — штатное завершение мониторинга.
        }
        catch (Exception ex)
        {
            ComparisonText.Text = $"Ошибка измерения: {ex.Message}";
        }
        finally
        {
            _monitoringCancellation?.Dispose();
            _monitoringCancellation = null;
            TargetsTextBox.IsEnabled = true;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _monitoringCancellation?.Cancel();

    private async void IdleTest_Click(object sender, RoutedEventArgs e)
    {
        await RunIdleExperimentAsync(compareWithKeepAlive: false);
    }

    private async void CompareKeepAliveTest_Click(object sender, RoutedEventArgs e)
    {
        await RunIdleExperimentAsync(compareWithKeepAlive: true);
    }

    private async Task RunIdleExperimentAsync(bool compareWithKeepAlive)
    {
        var host = IdleTargetTextBox.Text.Trim();
        if (!IsPlausibleHost(host))
        {
            IdleTestSummaryText.Text = "Введите корректный IP-адрес или DNS-адрес.";
            return;
        }

        if (_monitoringCancellation is not null || _keepAliveCancellation is not null)
        {
            IdleTestSummaryText.Text = "Остановите основной мониторинг и keepalive перед тестом простоя, чтобы не искажать результат.";
            return;
        }

        Uri? keepAliveEndpoint = null;
        var keepAliveIntervalSeconds = GetSelectedSeconds(KeepAliveIntervalComboBox, 10);
        if (compareWithKeepAlive)
        {
            if (!Uri.TryCreate(KeepAliveUrlTextBox.Text.Trim(), UriKind.Absolute, out keepAliveEndpoint)
                || keepAliveEndpoint.Scheme != Uri.UriSchemeHttps)
            {
                IdleTestSummaryText.Text = "Для сравнения укажите корректный HTTPS keepalive endpoint.";
                return;
            }

            if (keepAliveIntervalSeconds > GetSelectedSeconds(IdleIntervalComboBox, 15))
            {
                IdleTestSummaryText.Text = "Для A/B-теста интервал keepalive должен быть не больше выбранной паузы. Уменьшите интервал keepalive или увеличьте паузу.";
                return;
            }
        }

        var intervalSeconds = GetSelectedSeconds(IdleIntervalComboBox, 15);
        IdleRows.Clear();
        IdleTestSummaryText.Text = "Подготовка теста…";
        IdleTargetTextBox.IsEnabled = false;
        IdleIntervalComboBox.IsEnabled = false;
        KeepAliveUrlTextBox.IsEnabled = false;
        KeepAliveIntervalComboBox.IsEnabled = false;
        IdleTestButton.IsEnabled = false;
        CompareKeepAliveTestButton.IsEnabled = false;
        StopIdleTestButton.IsEnabled = true;
        var idleCancellation = new CancellationTokenSource();
        _idleTestCancellation = idleCancellation;

        try
        {
            var noKeepAliveDeltas = new List<long>();
            var withKeepAliveDeltas = new List<long>();
            for (var round = 1; round <= IdleTestRounds; round++)
            {
                var token = idleCancellation.Token;
                IdleTestSummaryText.Text = $"Цикл {round}/{IdleTestRounds}: измеряю без keepalive…";
                var warm = await _monitor.MeasureAsync(host, token);
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), token);
                var afterIdle = await _monitor.MeasureAsync(host, token);
                var noKeepAliveDelta = GetLatencyDelta(warm, afterIdle);
                if (noKeepAliveDelta is { } deltaWithout)
                    noKeepAliveDeltas.Add(deltaWithout);
                IdleRows.Add(CreateIdleRow("Без keepalive", round, warm, afterIdle, noKeepAliveDelta));

                if (!compareWithKeepAlive || keepAliveEndpoint is null)
                    continue;

                IdleTestSummaryText.Text = $"Цикл {round}/{IdleTestRounds}: проверяю с keepalive раз в {keepAliveIntervalSeconds} с…";
                var keepAliveWarm = await _monitor.MeasureAsync(host, token);
                await WaitWithKeepAliveAsync(
                    keepAliveEndpoint,
                    TimeSpan.FromSeconds(intervalSeconds),
                    TimeSpan.FromSeconds(keepAliveIntervalSeconds),
                    token);
                var keepAliveAfterIdle = await _monitor.MeasureAsync(host, token);
                var keepAliveDelta = GetLatencyDelta(keepAliveWarm, keepAliveAfterIdle);
                if (keepAliveDelta is { } deltaWith)
                    withKeepAliveDeltas.Add(deltaWith);
                IdleRows.Add(CreateIdleRow("С keepalive", round, keepAliveWarm, keepAliveAfterIdle, keepAliveDelta));
            }

            IdleTestSummaryText.Text = compareWithKeepAlive
                ? FormatKeepAliveComparison(intervalSeconds, noKeepAliveDeltas, withKeepAliveDeltas)
                : FormatIdleSummary(intervalSeconds, noKeepAliveDeltas);
        }
        catch (OperationCanceledException)
        {
            IdleTestSummaryText.Text = "Тест остановлен. Уже полученные циклы сохранены на экране.";
        }
        catch (Exception ex)
        {
            IdleTestSummaryText.Text = $"Ошибка теста простоя: {ex.Message}";
        }
        finally
        {
            idleCancellation.Dispose();
            _idleTestCancellation = null;
            IdleTargetTextBox.IsEnabled = true;
            IdleIntervalComboBox.IsEnabled = true;
            KeepAliveUrlTextBox.IsEnabled = true;
            KeepAliveIntervalComboBox.IsEnabled = true;
            IdleTestButton.IsEnabled = true;
            CompareKeepAliveTestButton.IsEnabled = true;
            StopIdleTestButton.IsEnabled = false;
        }
    }

    private static IdleLatencyRow CreateIdleRow(string mode, int round, PingSample warm, PingSample afterIdle, long? delta) =>
        new(mode, round.ToString(), FormatMilliseconds(warm.LatencyMilliseconds),
            FormatMilliseconds(afterIdle.LatencyMilliseconds),
            delta is { } difference ? $"{difference:+0;-0;0} ms" : "нет пары ответов");

    private static long? GetLatencyDelta(PingSample warm, PingSample afterIdle) =>
        warm.LatencyMilliseconds is { } warmMs && afterIdle.LatencyMilliseconds is { } idleMs
            ? idleMs - warmMs
            : null;

    private static string FormatIdleSummary(int intervalSeconds, IReadOnlyList<long> deltas) => deltas.Count == 0
        ? "Не удалось получить достаточно парных ICMP-ответов. Повторите тест или выберите другую цель."
        : $"Средний сдвиг после паузы {intervalSeconds} с: {deltas.Average():+0.#;-0.#;0} ms по {deltas.Count} успешным парам. Положительное значение означает, что ping после паузы был выше.";

    private static string FormatKeepAliveComparison(
        int intervalSeconds,
        IReadOnlyList<long> withoutKeepAlive,
        IReadOnlyList<long> withKeepAlive)
    {
        if (withoutKeepAlive.Count == 0 || withKeepAlive.Count == 0)
            return "Недостаточно парных ICMP-ответов в одном из режимов для сравнения.";

        var baselineAverage = withoutKeepAlive.Average();
        var keepAliveAverage = withKeepAlive.Average();
        var averageBenefit = baselineAverage - keepAliveAverage;
        var medianWithout = Median(withoutKeepAlive);
        var medianWith = Median(withKeepAlive);
        var interpretation = averageBenefit > 0
            ? "В этой серии keepalive уменьшил средний сдвиг."
            : averageBenefit < 0
                ? "В этой серии keepalive не улучшил средний сдвиг."
                : "В этой серии средние значения совпали.";
        return $"Пауза: {intervalSeconds} с\nБез keepalive — среднее {baselineAverage:+0.#;-0.#;0} мс, медиана {medianWithout:+0.#;-0.#;0} мс\nС keepalive — среднее {keepAliveAverage:+0.#;-0.#;0} мс, медиана {medianWith:+0.#;-0.#;0} мс\nРазница среднего (без − с): {averageBenefit:+0.#;-0.#;0} мс. Плюс означает улучшение с keepalive. {interpretation}\nКороткий эксперимент; результат не доказывает изменение состояния радиомодуля.";
    }

    private static double Median(IReadOnlyList<long> values)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2d
            : sorted[middle];
    }

    private static async Task WaitWithKeepAliveAsync(
        Uri endpoint,
        TimeSpan pause,
        TimeSpan keepAliveInterval,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < pause)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await TrySendKeepAliveProbeAsync(endpoint, cancellationToken);

            var remaining = pause - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;

            await Task.Delay(remaining < keepAliveInterval ? remaining : keepAliveInterval, cancellationToken);
        }
    }

    private static async Task TrySendKeepAliveProbeAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var response = await KeepAliveClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (HttpRequestException)
        {
            // Сбой endpoint фиксируется косвенно: сравнение всё равно продолжает измерять ICMP.
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Таймаут отдельного HTTPS-запроса не отменяет весь эксперимент.
        }
    }

    private void StopIdleTest_Click(object sender, RoutedEventArgs e) => _idleTestCancellation?.Cancel();

    private async void StartKeepAlive_Click(object sender, RoutedEventArgs e)
    {
        var endpointText = KeepAliveUrlTextBox.Text.Trim();
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            KeepAliveStatusText.Text = "Укажите корректный HTTPS endpoint. Небезопасный HTTP не поддерживается.";
            return;
        }

        if (_idleTestCancellation is not null || _monitoringCancellation is not null)
        {
            KeepAliveStatusText.Text = "Сначала остановите тест задержки после простоя.";
            return;
        }

        var intervalSeconds = GetSelectedSeconds(KeepAliveIntervalComboBox, 30);
        KeepAliveUrlTextBox.IsEnabled = false;
        KeepAliveIntervalComboBox.IsEnabled = false;
        StartKeepAliveButton.IsEnabled = false;
        StopKeepAliveButton.IsEnabled = true;
        KeepAliveStatusText.Text = $"Активен: запрос раз в {intervalSeconds} с. Оценка сетевого расхода будет приблизительной.";
        var keepAliveCancellation = new CancellationTokenSource();
        _keepAliveCancellation = keepAliveCancellation;

        try
        {
            var requestCount = 0;
            var successCount = 0;
            long declaredBodyBytes = 0;

            while (true)
            {
                keepAliveCancellation.Token.ThrowIfCancellationRequested();
                requestCount++;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                    using var response = await KeepAliveClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        keepAliveCancellation.Token);

                    if (response.IsSuccessStatusCode)
                        successCount++;
                    declaredBodyBytes += response.Content.Headers.ContentLength ?? 0;
                    KeepAliveStatusText.Text = $"Активен · запросов: {requestCount}, успешных: {successCount}, последний HTTP {(int)response.StatusCode}; тело ответов: {declaredBodyBytes} байт. Фактический расход выше из-за HTTPS/IP-заголовков.";
                }
                catch (HttpRequestException ex)
                {
                    KeepAliveStatusText.Text = $"Активен · запросов: {requestCount}, успешных: {successCount}; последняя ошибка: {ex.Message}";
                }
                catch (OperationCanceledException ex) when (!keepAliveCancellation.IsCancellationRequested)
                {
                    KeepAliveStatusText.Text = $"Активен · запросов: {requestCount}, успешных: {successCount}; таймаут HTTPS-запроса: {ex.Message}";
                }

                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), keepAliveCancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            KeepAliveStatusText.Text += " · остановлен.";
        }
        finally
        {
            keepAliveCancellation.Dispose();
            _keepAliveCancellation = null;
            KeepAliveUrlTextBox.IsEnabled = true;
            KeepAliveIntervalComboBox.IsEnabled = true;
            StartKeepAliveButton.IsEnabled = true;
            StopKeepAliveButton.IsEnabled = false;
        }
    }

    private void StopKeepAlive_Click(object sender, RoutedEventArgs e) => _keepAliveCancellation?.Cancel();

    private void OpenWaveform_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            "Waveform проверит задержку в простое и при загрузке канала почти до предела. На время теста интернет может замедлиться для всех устройств; тест может расходовать мобильный трафик. Запускайте его только когда это безопасно для вашей сети.\n\nОткрыть официальный тест Waveform в браузере?",
            "Тест bufferbloat создаёт нагрузку",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(WaveformBufferbloatUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ComparisonText.Text = $"Не удалось открыть браузер: {ex.Message}";
        }
    }

    private static int GetSelectedSeconds(System.Windows.Controls.ComboBox comboBox, int fallback)
    {
        if (comboBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string tag }
            && int.TryParse(tag, out var seconds))
            return seconds;

        return fallback;
    }

    private async Task MonitorTargetsAsync(IReadOnlyList<string> targets, CancellationToken cancellationToken)
    {
        while (true)
        {
            var cycleStarted = DateTimeOffset.UtcNow;
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sample = await _monitor.MeasureAsync(target, cancellationToken);
                _samples.Add(sample);

                var targetSampleCount = _samples.Count(item => item.Host.Equals(target, StringComparison.OrdinalIgnoreCase));
                if (targetSampleCount > HistoryLimitPerTarget)
                {
                    var oldest = _samples.FindIndex(item => item.Host.Equals(target, StringComparison.OrdinalIgnoreCase));
                    if (oldest >= 0)
                        _samples.RemoveAt(oldest);
                }
            }

            RefreshTargetRows(targets);
            var selectedHost = ChartTargetComboBox.SelectedItem as string ?? targets[0];
            UpdateMetricCards(GetStatistics(selectedHost));
            UpdateRecommendations(targets);
            DrawChart();

            var remaining = TimeSpan.FromSeconds(1) - (DateTimeOffset.UtcNow - cycleStarted);
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, cancellationToken);
        }
    }

    private void RefreshTargetRows(IReadOnlyList<string> targets)
    {
        TargetRows.Clear();
        foreach (var target in targets)
        {
            var stats = GetStatistics(target);
            if (stats is null)
            {
                TargetRows.Add(new TargetRow(target, "—", "—", "—", "—", "0"));
                continue;
            }

            TargetRows.Add(new TargetRow(
                target,
                FormatMilliseconds(stats.LatestMilliseconds),
                FormatMilliseconds(stats.AverageMilliseconds),
                FormatMilliseconds(stats.JitterMilliseconds),
                $"{stats.LossPercent:0.#}%",
                $"{stats.SuccessfulSamples}/{stats.TotalSamples}"));
        }
    }

    private TargetStatistics? GetStatistics(string host)
    {
        var recent = _samples
            .Where(sample => sample.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
            .TakeLast(StatisticsWindow)
            .ToArray();
        if (recent.Length == 0)
            return null;

        var successful = recent.Where(sample => sample.LatencyMilliseconds.HasValue)
            .Select(sample => sample.LatencyMilliseconds!.Value)
            .ToArray();
        var jitterPairs = recent.Zip(recent.Skip(1))
            .Where(pair => pair.First.LatencyMilliseconds.HasValue && pair.Second.LatencyMilliseconds.HasValue)
            .Select(pair => Math.Abs(pair.Second.LatencyMilliseconds!.Value - pair.First.LatencyMilliseconds!.Value))
            .ToArray();

        return new TargetStatistics(
            successful.Length == 0 ? null : successful[^1],
            successful.Length == 0 ? null : successful.Average(),
            jitterPairs.Length == 0 ? null : jitterPairs.Average(),
            100d * (recent.Length - successful.Length) / recent.Length,
            successful.Length,
            recent.Length);
    }

    private void UpdateMetricCards(TargetStatistics? stats)
    {
        PingValue.Text = stats?.LatestMilliseconds is { } ping ? $"{ping} ms" : "—";
        JitterValue.Text = stats?.JitterMilliseconds is { } jitter ? $"{jitter:0.#} ms" : "—";
        LossValue.Text = stats is null ? "—" : $"{stats.LossPercent:0.#}%";
    }

    private void UpdateRecommendations(IReadOnlyList<string> targets)
    {
        var summaries = targets
            .Select(target => (Host: target, Statistics: GetStatistics(target)))
            .Where(item => item.Statistics is not null)
            .Select(item => (item.Host, item.Statistics!))
            .ToArray();
        RecommendationsList.ItemsSource = RecommendationService.Create(summaries);
    }

    private void DrawChart()
    {
        if (ChartCanvas is null)
            return;

        ChartCanvas.Children.Clear();
        var width = ChartCanvas.ActualWidth;
        var height = ChartCanvas.ActualHeight;
        if (width < 20 || height < 20)
            return;

        const double left = 32;
        const double right = 6;
        const double top = 5;
        const double bottom = 17;
        var plotWidth = Math.Max(1, width - left - right);
        var plotHeight = Math.Max(1, height - top - bottom);

        for (var line = 0; line <= 3; line++)
        {
            var y = top + plotHeight * line / 3;
            ChartCanvas.Children.Add(new Line
            {
                X1 = left,
                X2 = width - right,
                Y1 = y,
                Y2 = y,
                Stroke = new SolidColorBrush(Color.FromRgb(231, 233, 237)),
                StrokeThickness = 1
            });
        }

        var host = ChartTargetComboBox.SelectedItem as string;
        if (host is null)
            return;

        var samples = _samples
            .Where(sample => sample.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
            .TakeLast(60)
            .ToArray();
        var values = samples.Where(sample => sample.LatencyMilliseconds.HasValue)
            .Select(sample => (double)sample.LatencyMilliseconds!.Value)
            .ToArray();
        if (values.Length == 0)
            return;

        var max = Math.Max(50, values.Max() * 1.15);
        var segment = new Polyline
        {
            Stroke = (Brush)FindResource("AccentBrush"),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            SnapsToDevicePixels = true
        };

        for (var index = 0; index < samples.Length; index++)
        {
            if (samples[index].LatencyMilliseconds is not { } latency)
            {
                AddSegment();
                continue;
            }

            var x = left + plotWidth * index / Math.Max(1, samples.Length - 1);
            var y = top + plotHeight - Math.Min(latency, max) / max * plotHeight;
            segment.Points.Add(new Point(x, y));
        }
        AddSegment();

        void AddSegment()
        {
            if (segment.Points.Count > 0)
                ChartCanvas.Children.Add(segment);
            segment = new Polyline
            {
                Stroke = (Brush)FindResource("AccentBrush"),
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
                SnapsToDevicePixels = true
            };
        }
    }

    private void ChartTarget_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsInitialized || ChartCanvas is null)
            return;

        var host = ChartTargetComboBox.SelectedItem as string;
        ChartCaption.Text = host is null
            ? "ICMP · последние измерения"
            : $"ICMP · последние 60 измерений · {host}";
        UpdateMetricCards(host is null ? null : GetStatistics(host));
        DrawChart();
    }

    private void ResultsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is TargetRow row && ChartTargetComboBox.Items.Contains(row.Host))
            ChartTargetComboBox.SelectedItem = row.Host;
    }

    private async void TraceRoute_Click(object sender, RoutedEventArgs e)
    {
        var host = RouteHostTextBox.Text.Trim();
        if (!IsPlausibleHost(host))
        {
            ComparisonText.Text = "Введите корректный IP-адрес или DNS-адрес для traceroute.";
            return;
        }

        RouteHostTextBox.IsEnabled = false;
        TraceButton.IsEnabled = false;
        RouteRows.Clear();
        ComparisonText.Text = "Traceroute выполняет по одному ICMP-запросу на каждый шаг; некоторые узлы могут не отвечать.";

        try
        {
            var progress = new Progress<RouteHopResult>(hop => RouteRows.Add(new RouteHopRow(
                hop.Hop,
                hop.Address,
                hop.LatencyMilliseconds is { } ms ? $"{ms} ms" : "—",
                hop.Status)));
            var reached = await _routeTrace.TraceAsync(host, progress, CancellationToken.None);
            ComparisonText.Text = reached
                ? $"Маршрут до {host} завершён: получен ответ от цели."
                : $"Достигнут лимит шагов для {host}; это не обязательно означает неисправность маршрута.";
        }
        catch (Exception ex)
        {
            ComparisonText.Text = $"Не удалось выполнить traceroute: {ex.Message}";
        }
        finally
        {
            RouteHostTextBox.IsEnabled = true;
            TraceButton.IsEnabled = true;
        }
    }

    private void CaptureBaseline_Click(object sender, RoutedEventArgs e)
    {
        var targets = TargetRows.Select(row => row.Host).ToArray();
        if (targets.Length == 0 || targets.All(target => GetStatistics(target) is null))
        {
            ComparisonText.Text = "Сначала запустите измерения и дождитесь нескольких результатов.";
            return;
        }

        _baseline.Clear();
        foreach (var target in targets)
        {
            var stats = GetStatistics(target);
            if (stats is not null)
                _baseline[target] = new BaselineSnapshot(stats, DateTimeOffset.Now);
        }

        ComparisonText.Text = $"Исходный снимок сохранён локально в памяти: {DateTimeOffset.Now:HH:mm:ss}. Запустите тест повторно после одного изменения настройки, чтобы сравнить результат.";
    }

    private void CompareBaseline_Click(object sender, RoutedEventArgs e)
    {
        if (_baseline.Count == 0)
        {
            ComparisonText.Text = "Сначала снимите исходный замер кнопкой «Сохранить ДО».";
            return;
        }

        var lines = new List<string>();
        foreach (var (host, snapshot) in _baseline)
        {
            var afterSamples = _samples
                .Where(sample => sample.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
                                 && sample.Timestamp > snapshot.CapturedAt)
                .TakeLast(StatisticsWindow)
                .ToArray();
            var after = CalculateStatistics(afterSamples);
            var before = snapshot.Statistics;
            if (after is null || before.AverageMilliseconds is null || after.AverageMilliseconds is null)
            {
                lines.Add($"{host}: недостаточно успешных ответов для сравнения.");
                continue;
            }

            var pingDelta = after.AverageMilliseconds.Value - before.AverageMilliseconds.Value;
            var jitterDelta = DeltaText(before.JitterMilliseconds, after.JitterMilliseconds);
            var lossDelta = after.LossPercent - before.LossPercent;
            var direction = pingDelta < 0 ? "лучше" : pingDelta > 0 ? "хуже" : "без изменения";
            lines.Add($"{host}: средний ping {direction} на {Math.Abs(pingDelta):0.#} ms; джиттер {jitterDelta}; потери {lossDelta:+0.#;-0.#;0}%.");
        }

        ComparisonText.Text = string.Join(Environment.NewLine, lines);
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_samples.Count == 0)
        {
            ComparisonText.Text = "Пока нет измерений для экспорта.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Экспорт результатов PingBoost",
            FileName = $"pingboost-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            Filter = "CSV (*.csv)|*.csv",
            DefaultExt = ".csv",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            CsvReportService.Export(dialog.FileName, _samples);
            ComparisonText.Text = $"CSV отчёт сохранён: {dialog.FileName}";
        }
        catch (IOException ex)
        {
            ComparisonText.Text = $"Не удалось сохранить CSV: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            ComparisonText.Text = $"Нет доступа для сохранения CSV: {ex.Message}";
        }
    }

    private static IReadOnlyList<string> ParseTargets(string text)
    {
        return text.Split(new[] { ',', ';', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsPlausibleHost(string host)
    {
        if (host.Length is 0 or > 253)
            return false;
        if (IPAddress.TryParse(host, out _))
            return true;

        return Uri.CheckHostName(host) == UriHostNameType.Dns
               && host.All(character => char.IsLetterOrDigit(character) || character is '-' or '.');
    }

    private static string FormatMilliseconds(double? value) => value is null ? "—" : $"{value:0.#} ms";

    private static string DeltaText(double? before, double? after)
    {
        if (before is null || after is null)
            return "недостаточно данных";
        return $"{after.Value - before.Value:+0.#;-0.#;0} ms";
    }

    private static TargetStatistics? CalculateStatistics(IReadOnlyList<PingSample> samples)
    {
        if (samples.Count == 0)
            return null;

        var successful = samples.Where(sample => sample.LatencyMilliseconds.HasValue)
            .Select(sample => sample.LatencyMilliseconds!.Value)
            .ToArray();
        var jitterPairs = samples.Zip(samples.Skip(1))
            .Where(pair => pair.First.LatencyMilliseconds.HasValue && pair.Second.LatencyMilliseconds.HasValue)
            .Select(pair => Math.Abs(pair.Second.LatencyMilliseconds!.Value - pair.First.LatencyMilliseconds!.Value))
            .ToArray();

        return new TargetStatistics(
            successful.Length == 0 ? null : successful[^1],
            successful.Length == 0 ? null : successful.Average(),
            jitterPairs.Length == 0 ? null : jitterPairs.Average(),
            100d * (samples.Count - successful.Length) / samples.Count,
            successful.Length,
            samples.Count);
    }
}

public sealed record TargetStatistics(
    long? LatestMilliseconds,
    double? AverageMilliseconds,
    double? JitterMilliseconds,
    double LossPercent,
    int SuccessfulSamples,
    int TotalSamples);

public sealed record TargetRow(string Host, string LatestText, string AverageText,
    string JitterText, string LossText, string SamplesText);

public sealed record RouteHopRow(int Hop, string Address, string LatencyText, string Status);

public sealed record BaselineSnapshot(TargetStatistics Statistics, DateTimeOffset CapturedAt);

public sealed record IdleLatencyRow(string Mode, string Round, string WarmText, string IdleText, string DeltaText);

public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
