using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PingBoost.Services;

public static class LocalizationService
{
    private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Тесты"] = "Tests", ["Обзор и сравнение"] = "Overview & comparison", ["Маршрут"] = "Route",
        ["ГОТОВО К ТЕСТУ"] = "READY TO TEST", ["ИДЁТ ИЗМЕРЕНИЕ"] = "MEASURING",
        ["СЕТЬ И АДАПТЕРЫ"] = "NETWORK & ADAPTERS", ["Обновить"] = "Refresh", ["ЦЕЛИ ТЕСТА"] = "TEST TARGETS",
        ["Укажите IP-адреса или DNS-имена через запятую или с новой строки."] = "Enter IP addresses or DNS names separated by commas or new lines.",
        ["Начать тест"] = "Start test", ["Остановить"] = "Stop", ["ПОСЛЕДНИЙ PING"] = "LATEST PING",
        ["СРЕДНИЙ ДЖИТТЕР"] = "AVERAGE JITTER", ["ПОТЕРИ"] = "LOSS", ["ЗАДЕРЖКА ВО ВРЕМЕНИ"] = "LATENCY OVER TIME",
        ["СРАВНЕНИЕ ЦЕЛЕЙ"] = "TARGET COMPARISON", ["БЕЗОПАСНЫЕ РЕКОМЕНДАЦИИ"] = "SAFE RECOMMENDATIONS",
        ["РЕЗУЛЬТАТЫ ДО / ПОСЛЕ"] = "BEFORE / AFTER RESULTS", ["Сохранить ДО"] = "Save baseline",
        ["Сравнить ПОСЛЕ"] = "Compare after", ["Экспорт CSV"] = "Export CSV", ["ЦЕЛЬ"] = "TARGET",
        ["Сейчас"] = "Latest", ["Среднее"] = "Average", ["Джиттер"] = "Jitter", ["Ответы"] = "Replies",
        ["Задержка после простоя"] = "Latency after idle", ["ЗАДЕРЖКА ПОСЛЕ ПРОСТОЯ"] = "LATENCY AFTER IDLE",
        ["Запустить 5 циклов"] = "Run 5 rounds", ["Сравнить keepalive"] = "Compare keepalive",
        ["ОПЦИОНАЛЬНЫЙ KEEPALIVE"] = "OPTIONAL KEEPALIVE", ["Включить"] = "Enable",
        ["Выключен. Запросы не отправляются."] = "Off. No requests are sent.",
        ["ЗАПИСЬ ИГРОВОЙ СЕССИИ"] = "GAME SESSION RECORDING", ["Начать запись"] = "Start recording",
        ["Снять / сравнить маршруты"] = "Capture / compare routes", ["Профиль"] = "Profile",
        ["Процесс"] = "Process", ["Спокойно"] = "Calm", ["Начался бой"] = "Combat started",
        ["Заметил потери"] = "Packet loss noticed", ["Экспорт сессии CSV"] = "Export session CSV",
        ["Время"] = "Time", ["Событие / цель"] = "Event / target", ["Ping"] = "Ping", ["Статус"] = "Status",
        ["Проверка маршрута (traceroute)"] = "Route check (traceroute)", ["Проверить маршрут"] = "Trace route",
        ["Укажите отвечающие узлы по пути. Таймаут или отсутствие ответа на промежуточном узле не обязательно означает потерю игрового трафика."] = "Shows responding hops along the path. A timeout or missing reply from an intermediate hop does not necessarily mean game traffic is lost.",
        ["Сравнивает ping сразу после пробуждения с ping после короткой паузы. Повторите несколько циклов, чтобы увидеть, добавляет ли сотовая сеть задержку после простоя."] = "Compares ping immediately after activity with ping after a short idle period. Repeat cycles to see whether the cellular network adds latency after idle.",
        ["Параллельно проверяет локальный шлюз и внешний узел, чтобы понять, где возникают задержки или пропуски ICMP. Во время игры отмечайте события кнопками ниже. Это диагностические ICMP-пробы, не захват игровых UDP-пакетов."] = "Checks the local gateway and an external host to help locate ICMP delays or timeouts. Mark gameplay events below. These are diagnostic ICMP probes, not a capture of game UDP packets.",
        ["Сессия также отмечает доступные системные события и IPv4 UDP локальные порты выбранного процесса. Снимок маршрута — ручной traceroute; на время снимка обычные пробы приостанавливаются."] = "The session also records available system events and IPv4 UDP local ports for the selected process. Route snapshots use manual traceroute and pause regular probes temporarily.",
        ["Периодически отправляет небольшой HTTPS-запрос. Может помочь удержать соединение активным, но результат зависит от модема и оператора; это расходует трафик и заряд. По умолчанию выключен."] = "Sends a small HTTPS request periodically. It may keep a connection active depending on the modem and carrier, and uses data and battery. Off by default.",
        ["Для чистого теста задержки после простоя остановите keepalive. Адрес должен быть HTTPS; используйте только доверенный endpoint."] = "Stop keepalive for a clean idle-latency test. Use HTTPS and only a trusted endpoint.",
        ["BUFFERBLOAT · ЗАДЕРЖКА ПОД НАГРУЗКОЙ"] = "BUFFERBLOAT · LATENCY UNDER LOAD",
        ["Официальный тест Waveform измеряет idle / download / upload latency и jitter, пока канал нагружается. Результат и оценка A+–F показываются на сайте; PingBoost не считывает их автоматически."] = "The official Waveform test measures idle/download/upload latency and jitter while loading the connection. Waveform displays the result and A+–F grade; PingBoost does not read them automatically.",
        ["Открыть Waveform…"] = "Open Waveform…", ["Шлюз"] = "Gateway",
        ["Внешний узел"] = "External host", ["Пауза"] = "Pause", ["Цикл"] = "Round",
        ["Сразу после трафика"] = "After activity", ["После паузы"] = "After idle", ["Разница"] = "Delta",
        ["Интервал"] = "Interval",
        ["Настройки"] = "Settings", ["Язык интерфейса"] = "Interface language",
        ["Русский"] = "Russian", ["Запускать вместе с Windows (текущий пользователь)"] = "Start with Windows (current user)",
        ["Профили игр сохраняются локально и содержат параметры целей, записи сессии, теста простоя и keepalive. Профиль не меняет маршрутизацию игрового трафика."] = "Game profiles are stored locally and include target, session, idle-test, and keepalive settings. Profiles do not change game traffic routing.",
        ["Новый профиль"] = "New profile", ["Название профиля"] = "Profile name",
        ["Имя процесса игры (необязательно, без .exe)"] = "Game process name (optional, without .exe)",
        ["Цели мониторинга (по одной на строку)"] = "Monitoring targets (one per line)",
        ["Шлюз hotspot / роутера"] = "Hotspot/router gateway", ["Внешняя цель записи сессии"] = "External session target",
        ["Цель теста после простоя"] = "Idle test target", ["Пауза теста простоя (секунды)"] = "Idle test pause (seconds)",
        ["HTTPS keepalive endpoint"] = "HTTPS keepalive endpoint", ["Интервал keepalive (секунды)"] = "Keepalive interval (seconds)",
        ["Удалить профиль"] = "Delete profile", ["Сохранить настройки"] = "Save settings",
        ["ROUTE COMPARISON"] = "ROUTE COMPARISON"
    };

    public static void Apply(DependencyObject root, string language)
    {
        var translations = language == "en"
            ? English
            : English.GroupBy(pair => pair.Value, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal);
        Visit(root, translations);
    }

    private static void Visit(DependencyObject node, IReadOnlyDictionary<string, string> translations)
    {
        if (node is TextBlock text && translations.TryGetValue(text.Text, out var translated))
            text.Text = translated;
        if (node is ContentControl content && content.Content is string value && translations.TryGetValue(value, out translated))
            content.Content = translated;
        if (node is TabItem tab && tab.Header is string header && translations.TryGetValue(header, out translated))
            tab.Header = translated;
        if (node is DataGrid grid)
            foreach (var column in grid.Columns)
                if (column.Header is string columnHeader && translations.TryGetValue(columnHeader, out translated))
                    column.Header = translated;

        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            Visit(child, translations);
    }
}
