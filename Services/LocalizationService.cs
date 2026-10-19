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
        ["Начать тест"] = "Start test", ["Остановить"] = "Stop", ["ПОСЛЕДНИЙ PING"] = "LATEST PING",
        ["СРЕДНИЙ ДЖИТТЕР"] = "AVERAGE JITTER", ["ПОТЕРИ"] = "LOSS", ["ЗАДЕРЖКА ВО ВРЕМЕНИ"] = "LATENCY OVER TIME",
        ["РЕЗУЛЬТАТЫ ДО / ПОСЛЕ"] = "BEFORE / AFTER RESULTS", ["Сохранить ДО"] = "Save baseline",
        ["Сравнить ПОСЛЕ"] = "Compare after", ["Экспорт CSV"] = "Export CSV", ["ЦЕЛЬ"] = "TARGET",
        ["Сейчас"] = "Latest", ["Среднее"] = "Average", ["Джиттер"] = "Jitter", ["Ответы"] = "Replies",
        ["Задержка после простоя"] = "Latency after idle", ["ЗАДЕРЖКА ПОСЛЕ ПРОСТОЯ"] = "LATENCY AFTER IDLE",
        ["Запустить 5 циклов"] = "Run 5 rounds", ["Сравнить keepalive"] = "Compare keepalive",
        ["ОПЦИОНАЛЬНЫЙ KEEPALIVE"] = "OPTIONAL KEEPALIVE", ["Включить"] = "Enable",
        ["Выключен. Запросы не отправляются."] = "Off. No requests are sent.",
        ["Процесс"] = "Process", ["Спокойно"] = "Calm", ["Начался бой"] = "Combat started",
        ["Проверка маршрута (traceroute)"] = "Route check (traceroute)", ["Проверить маршрут"] = "Trace route",
        ["Укажите отвечающие узлы по пути. Таймаут или отсутствие ответа на промежуточном узле не обязательно означает потерю игрового трафика."] = "Shows responding hops along the path. A timeout or missing reply from an intermediate hop does not necessarily mean game traffic is lost.",
        ["Сравнивает ping сразу после пробуждения с ping после короткой паузы. Повторите несколько циклов, чтобы увидеть, добавляет ли сотовая сеть задержку после простоя."] = "Compares ping immediately after activity with ping after a short idle period. Repeat cycles to see whether the cellular network adds latency after idle.",
        ["Периодически отправляет небольшой HTTPS-запрос. Может помочь удержать соединение активным, но результат зависит от модема и оператора; это расходует трафик и заряд. По умолчанию выключен."] = "Sends a small HTTPS request periodically. It may keep a connection active depending on the modem and carrier, and uses data and battery. Off by default.",
        ["Для чистого теста задержки после простоя остановите keepalive. Адрес должен быть HTTPS; используйте только доверенный endpoint."] = "Stop keepalive for a clean idle-latency test. Use HTTPS and only a trusted endpoint.",
        ["BUFFERBLOAT · ЗАДЕРЖКА ПОД НАГРУЗКОЙ"] = "BUFFERBLOAT · LATENCY UNDER LOAD",
        ["Официальный тест Waveform измеряет idle / download / upload latency и jitter, пока канал нагружается. Результат и оценка A+–F показываются на сайте; PingBoost не считывает их автоматически."] = "The official Waveform test measures idle/download/upload latency and jitter while loading the connection. Waveform displays the result and A+–F grade; PingBoost does not read them automatically.",
        ["Открыть Waveform…"] = "Open Waveform…", ["Пауза"] = "Pause", ["Цикл"] = "Round",
        ["Сразу после трафика"] = "After activity", ["После паузы"] = "After idle", ["Разница"] = "Delta",
        ["Интервал"] = "Interval",
        ["Настройки"] = "Settings", ["Язык интерфейса"] = "Interface language",
        ["Русский"] = "Russian", ["Запускать вместе с Windows (текущий пользователь)"] = "Start with Windows (current user)",
        ["ROUTE COMPARISON"] = "ROUTE COMPARISON",
        ["▾ В трей"] = "▾ To tray",
        ["TCP: включить ECN"] = "TCP: enable ECN",
        ["Если оператор помечает перегрузку в пакете вместо отбрасывания, TCP снижает скорость до того, как очередь вырастет. Работает только если оператор поддерживает ECN; иначе ничего не меняется. Только TCP, действует на новые соединения."] = "If the carrier marks congestion in the packet instead of dropping it, TCP slows down before the queue grows. Works only if the carrier supports ECN; otherwise nothing changes. TCP only, applies to new connections.",
        ["Общий потолок отдачи всего ПК"] = "Overall upload ceiling for the whole PC",
        ["Потолок ниже реальной скорости отдачи не даёт компьютеру забивать очередь модема, и пинг под нагрузкой остаётся низким. Игровой трафик идёт на малой скорости и потолка не достигает. Скорость отдачи канала плавает, начните с 40 Мбит/с и проверьте тестом Waveform."] = "A ceiling below the real upload speed keeps the PC from filling the modem queue so ping under load stays low. Game traffic is low-rate and never reaches the ceiling. Link upload speed varies; start at 40 Mbit/s and check with the Waveform test.",
        ["Потолок отдачи ПК"] = "PC upload ceiling", ["Включить общий потолок"] = "Enable overall ceiling",
        ["20 Мбит/с"] = "20 Mbit/s", ["40 Мбит/с"] = "40 Mbit/s", ["45 Мбит/с"] = "45 Mbit/s", ["50 Мбит/с"] = "50 Mbit/s",
        ["Ограничить приём TCP: autotuning Restricted"] = "Limit TCP receive: autotuning Restricted",
        ["Окно приёма до 1 МБ: одно соединение не разгонится выше примерно 140 Мбит/с при пинге 60 мс. Скачивание меньше забивает очередь модема. Только TCP, действует на новые соединения."] = "Receive window up to 1 MB: a single connection cannot exceed roughly 140 Mbit/s at 60 ms ping. Downloads fill the modem queue less. TCP only, applies to new connections.",
        ["Ограничить приём TCP сильнее: autotuning HighlyRestricted"] = "Limit TCP receive harder: autotuning HighlyRestricted",
        ["Окно приёма до 256 КБ: примерно 35 Мбит/с на соединение при пинге 60 мс. Заметно снижает скорость загрузок, а пинг под нагрузкой обычно падает сильнее. Только TCP. Если отмечены оба пункта, применяется этот."] = "Receive window up to 256 KB: roughly 35 Mbit/s per connection at 60 ms ping. Noticeably slows downloads while ping under load usually drops more. TCP only. If both are checked, this one applies.",
        ["Отключить энергосбережение адаптера"] = "Disable adapter power saving",
        ["Запрещает Windows выключать адаптер для экономии энергии и отключает режимы экономии в драйвере (если драйвер их поддерживает)."] = "Stops Windows from turning the adapter off to save power and disables driver power-saving modes (if the driver supports them).",
        ["Отключить Interrupt Moderation"] = "Disable Interrupt Moderation",
        ["Адаптер сразу сообщает системе о каждом пакете, а не копит их. Снижает задержку и джиттер, немного повышает нагрузку на CPU."] = "The adapter reports every packet immediately instead of batching them. Lowers latency and jitter at a small CPU cost.",
        ["Отключить Energy-Efficient Ethernet / Green Ethernet"] = "Disable Energy-Efficient Ethernet / Green Ethernet",
        ["Убирает короткие «засыпания» проводного порта между пакетами."] = "Removes short wired-port sleep periods between packets.",
        ["Минимальная агрессивность роуминга Wi-Fi"] = "Lowest Wi-Fi roaming aggressiveness",
        ["Адаптер реже сканирует эфир и переключается между точками; уменьшает скачки пинга при игре через Wi-Fi."] = "The adapter scans and switches access points less often; reduces ping spikes when playing over Wi-Fi.",
        ["Отключить Large Send Offload"] = "Disable Large Send Offload",
        ["Пакеты формируются процессором, а не сетевой картой. Иногда убирает задержки пакетов при отправке."] = "Packets are built by the CPU instead of the network card. Sometimes removes send-side packet delays.",
        ["Отключить Receive Segment Coalescing глобально"] = "Disable Receive Segment Coalescing globally",
        ["Системная настройка RSC. Влияет на TCP-трафик, на UDP игры напрямую не влияет."] = "System-wide RSC setting. Affects TCP traffic; does not directly affect game UDP.",
        ["Отключить TCP timestamps"] = "Disable TCP timestamps",
        ["Из TCP-заголовков убираются 12 байт. Только TCP."] = "Removes 12 bytes from TCP headers. TCP only.",
        ["TCP: без задержки ACK и без алгоритма Nagle (выбранный адаптер)"] = "TCP: no delayed ACK and no Nagle algorithm (selected adapter)",
        ["TcpAckFrequency=1, TCPNoDelay=1, TcpDelAckTicks=0. Только TCP-игры и лаунчеры, UDP не затрагивается."] = "TcpAckFrequency=1, TCPNoDelay=1, TcpDelAckTicks=0. TCP games and launchers only; UDP is not affected.",
        ["MMCSS: снять сетевое ограничение и повысить отзывчивость"] = "MMCSS: remove network throttling and raise responsiveness",
        ["NetworkThrottlingIndex=отключено, SystemResponsiveness=10. Параметры из TCP Optimizer."] = "NetworkThrottlingIndex disabled, SystemResponsiveness=10. Settings from TCP Optimizer.",
        ["MMCSS: высокий приоритет задач Games"] = "MMCSS: high priority for Games tasks",
        ["GPU Priority 8, Priority 6, Scheduling Category High, SFIO Priority High."] = "GPU Priority 8, Priority 6, Scheduling Category High, SFIO Priority High.",
        ["Оптимизация"] = "Optimization", ["UDP-монитор"] = "UDP monitor", ["Статус"] = "Status", ["Сохранить настройки"] = "Save settings",
        ["ИГРА"] = "GAME",
        ["Процесс игры используется на вкладках «Оптимизация» и «UDP-монитор». Выберите его из списка (процессы с открытыми UDP-портами) или введите имя без .exe, например cs2."] = "The game process is used on the Optimization and UDP monitor tabs. Pick it from the list (processes with open UDP ports) or type the name without .exe, for example cs2.",
        ["2 секунды"] = "2 seconds",
        ["Для чистого теста задержки после простоя остановите keepalive. Адрес должен быть HTTPS; используйте только доверенный endpoint. Интервал 2 с расходует больше трафика и заряда."] = "Stop keepalive for a clean idle-latency test. Use HTTPS and only a trusted endpoint. A 2 s interval uses more data and battery.",
        ["Перезапустить от администратора"] = "Restart as administrator",
        ["Для изменения настроек адаптера и системы нужны права администратора. Измерения и просмотр состояния работают и без них."] = "Changing adapter and system settings requires administrator rights. Measurements and state viewing work without them.",
        ["НАСТРОЙКИ АДАПТЕРА И СИСТЕМЫ"] = "ADAPTER & SYSTEM SETTINGS",
        ["Перед первым изменением PingBoost сохраняет исходные значения. Любое изменение можно отменить кнопками внизу вкладки. Драйверы не устанавливаются."] = "PingBoost saves the original values before the first change. Every change can be undone with the buttons at the bottom of this tab. No drivers are installed.",
        ["Адаптер"] = "Adapter", ["Применить выбранное"] = "Apply selected", ["Показать текущее состояние"] = "Show current state",
        ["Галочка и пометка «включено» показывают, что пункт действует сейчас. Снять отдельный пункт нельзя, для этого есть кнопки отката ниже. Некоторые параметры перезапускают адаптер: соединение пропадёт на несколько секунд, не применяйте их во время матча."] = "A tick and the \"enabled\" mark show that the item is in effect now. A single item cannot be removed; use the restore buttons below. Some settings restart the adapter and the connection drops for a few seconds, so do not apply them during a match.",
        ["включено"] = "enabled",
        ["MTU БЕЗ ФРАГМЕНТАЦИИ"] = "MTU WITHOUT FRAGMENTATION", ["Подобрать MTU"] = "Find MTU", ["Применить найденный MTU"] = "Apply found MTU",
        ["Результат появится здесь."] = "The result will appear here.",
        ["Находит самый большой пакет, который проходит до узла без фрагментации (аналог ping -f -l). На мобильных каналах фрагментация UDP может давать потери. Для проверки выберите адрес игрового сервера или публичный узел."] = "Finds the largest packet that reaches the host without fragmentation (like ping -f -l). On mobile links UDP fragmentation can cause loss. Use a game server address or a public host.",
        ["QoS · ПОМЕТКА UDP-ТРАФИКА ИГРЫ (DSCP)"] = "QoS · GAME UDP TRAFFIC MARKING (DSCP)",
        ["Windows помечает исходящий UDP-трафик процесса игры (блок «Игра» на вкладке «Тесты») приоритетом DSCP. Помогает на Wi-Fi и в сетях, которые учитывают DSCP; оператор мобильной сети и hotspot-телефон метку могут сбросить."] = "Windows marks outgoing UDP traffic of the game process with a DSCP priority. It helps on Wi-Fi and networks that honor DSCP; mobile carriers and phone hotspots may clear the mark.",
        ["Включить для процесса игры"] = "Enable for game process", ["Убрать"] = "Remove", ["Политика не создана."] = "No policy created.",
        ["ОГРАНИЧЕНИЕ ОТДАЧИ ФОНОВЫХ ПРОГРАММ (BUFFERBLOAT)"] = "BACKGROUND UPLOAD LIMIT (BUFFERBLOAT)",
        ["Когда программа забивает исходящий канал, очередь у модема или оператора растёт и пинг в игре подскакивает. Windows ограничит скорость отдачи перечисленных программ политикой QoS. Ограничивается только исходящий трафик; входящий так не ограничить. Не добавляйте сюда саму игру."] = "When a program fills the uplink, the queue at the modem or carrier grows and in-game ping spikes. Windows will limit the upload rate of the listed programs with a QoS policy. Only outgoing traffic is limited; incoming cannot be limited this way. Do not add the game itself.",
        ["Программы (по одной на строку, без .exe)"] = "Programs (one per line, without .exe)",
        ["Лимит отдачи на программу"] = "Upload limit per program",
        ["2 Мбит/с"] = "2 Mbit/s", ["5 Мбит/с"] = "5 Mbit/s", ["10 Мбит/с"] = "10 Mbit/s", ["20 Мбит/с"] = "20 Mbit/s", ["30 Мбит/с"] = "30 Mbit/s",
        ["Применить ограничение"] = "Apply limit", ["Снять ограничения"] = "Remove limits", ["Ограничений нет."] = "No limits set.",
        ["Подсказка: при исходящей скорости около 58 Мбит/с держите суммарный лимит программ заметно ниже неё. После применения повторите тест Waveform и сравните оценку."] = "Tip: with an uplink of about 58 Mbit/s keep the combined program limits well below it. After applying, repeat the Waveform test and compare the grade.",
        ["ИГРОВОЙ РЕЖИМ"] = "GAME MODE",
        ["Временные изменения на время игры. При выключении всё возвращается; если приложение закрыть, режим выключается автоматически."] = "Temporary changes while you play. Everything is restored when turned off; closing the app turns it off automatically.",
        ["Высокий приоритет процесса игры"] = "High priority for the game process",
        ["Схема питания «Высокая производительность» и Wi-Fi без энергосбережения"] = "High performance power plan and Wi-Fi without power saving",
        ["Остановить фоновые загрузки (Центр обновления, BITS, Delivery Optimization)"] = "Pause background downloads (Windows Update, BITS, Delivery Optimization)",
        ["Включить игровой режим"] = "Enable game mode", ["Выключен."] = "Off.",
        ["ОТКАТ НАСТРОЕК"] = "RESTORE SETTINGS",
        ["«Вернуть как было» восстанавливает значения, сохранённые перед первым изменением. «Стандартные настройки Windows» выставляет документированные значения по умолчанию для всех параметров, которыми управляет PingBoost, даже если резервной копии нет."] = "\"Restore previous\" brings back the values saved before the first change. \"Windows defaults\" sets documented default values for every setting PingBoost manages, even without a backup.",
        ["Вернуть как было до PingBoost"] = "Restore state from before PingBoost",
        ["Сбросить к стандартным настройкам Windows"] = "Reset to Windows defaults",
        ["UDP-трафик игры"] = "Game UDP traffic", ["Начать наблюдение"] = "Start monitoring",
        ["Показывает адреса и порты, с которыми игра обменивается UDP-пакетами, а также скорость приёма и максимальную паузу между принятыми пакетами за последние 10 секунд. Читаются только заголовки (адреса, порты, размер, время), содержимое пакетов не анализируется. Игровой сервер — строка с наибольшим числом пакетов."] = "Shows addresses and ports the game exchanges UDP packets with, the receive rate and the longest gap between received packets over the last 10 seconds. Only headers (addresses, ports, size, timing) are read; packet contents are not inspected. The game server is the row with the most packets.",
        ["СРАВНЕНИЕ ЦЕЛЕЙ"] = "TARGET COMPARISON",
        ["Готовые DNS-серверы и свои адреса"] = "Ready-made DNS servers and your own addresses", ["Добавить"] = "Add",
        ["Не запущено (нужны права администратора)."] = "Not running (administrator rights required).",
        ["Измерения ничего не меняют. «Оптимизация» меняет настройки только после подтверждения и всегда откатывается. Результат не гарантирован."] = "Measurements change nothing. Optimization changes settings only after confirmation and can always be rolled back. Results are not guaranteed.",
        ["Джиттер приёма"] = "Receive jitter", ["ЛОКАЛЬНЫЕ ПОТЕРИ ЭТОГО ПК"] = "LOCAL LOSS ON THIS PC", ["Появится после начала наблюдения."] = "Appears after monitoring starts.",
        ["Удалённый адрес"] = "Remote address", ["Отправлено"] = "Sent", ["Принято"] = "Received", ["Приём, пак/с"] = "Receive, pkt/s",
        ["Макс. пауза приёма"] = "Max receive gap", ["Последний пакет"] = "Last packet",
        ["Пауза приёма в сотни миллисекунд при активной игре означает, что от сервера не приходили пакеты: потеря или задержка на пути от сервера. Это не видно по ICMP-пингу до роутера или 1.1.1.1. Сравнивайте паузы с ощущаемыми лагами."] = "A receive gap of hundreds of milliseconds during active play means no packets arrived from the server: loss or delay on the path from the server. ICMP ping to the router or 1.1.1.1 does not show this. Compare gaps with the lags you feel.",
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
        if (node is HeaderedContentControl headered && headered.Header is string header && translations.TryGetValue(header, out translated))
            headered.Header = translated;
        if (node is DataGrid grid)
            foreach (var column in grid.Columns)
                if (column.Header is string columnHeader && translations.TryGetValue(columnHeader, out translated))
                    column.Header = translated;

        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            Visit(child, translations);
    }
}
