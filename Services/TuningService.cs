using System.Text.Json;
using System.IO;
using Microsoft.Win32;

namespace PingBoost.Services;

public sealed record AdapterInfo(string Name, string Description, string Guid)
{
    public override string ToString() => $"{Name} — {Description}";
}

public sealed record TweakDefinition(string Id, string Title, string Description, bool NeedsAdapter);

public sealed record RegistryValueSpec(string Path, string Name, RegistryValueKind Kind, object Desired, object? WindowsDefault);

/// <summary>
/// Applies reversible Windows network tweaks. Every change first stores the previous value in
/// <see cref="TuningBackupStore"/>, so "restore previous" and "Windows defaults" are always available.
/// No driver is installed: everything uses documented PowerShell cmdlets and registry values.
/// </summary>
public sealed class TuningService
{
    private const string MultimediaPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string GamesPath = MultimediaPath + @"\Tasks\Games";
    private const string PschedPath = @"SOFTWARE\Policies\Microsoft\Windows\Psched";
    private const string QosPath = @"SYSTEM\CurrentControlSet\Services\Tcpip\QoS";
    private const string InterfacesPath = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private const string TcpSetting = "InternetCustom";
    private const string QosPolicyPrefix = "PingBoost-";
    private const string ThrottlePolicyPrefix = QosPolicyPrefix + "Limit-";
    private const string HighPerformancePlan = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string BalancedPlan = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private static readonly string[] PausableServices = ["wuauserv", "BITS", "DoSvc"];

    private static readonly Dictionary<string, Dictionary<string, string>> AdvancedTweaks = new()
    {
        ["ad-power"] = new() { ["*SelectiveSuspend"] = "0", ["PowerSavingMode"] = "0" },
        ["ad-irq"] = new() { ["*InterruptModeration"] = "0" },
        ["ad-eee"] = new() { ["*EEE"] = "0", ["EnableGreenEthernet"] = "0", ["GreenEthernet"] = "0", ["GigaLite"] = "0", ["AdvancedEEE"] = "0" },
        ["ad-roam"] = new() { ["RoamAggressiveness"] = "1" }
    };

    public static IReadOnlyList<TweakDefinition> Tweaks { get; } =
    [
        new("ad-power", "Отключить энергосбережение адаптера", "Запрещает Windows выключать адаптер для экономии энергии и отключает режимы экономии в драйвере (если драйвер их поддерживает).", true),
        new("ad-irq", "Отключить Interrupt Moderation", "Адаптер сразу сообщает системе о каждом пакете, а не копит их. Снижает задержку и джиттер, немного повышает нагрузку на CPU.", true),
        new("ad-eee", "Отключить Energy-Efficient Ethernet / Green Ethernet", "Убирает короткие «засыпания» проводного порта между пакетами.", true),
        new("ad-roam", "Минимальная агрессивность роуминга Wi-Fi", "Адаптер реже сканирует эфир и переключается между точками; уменьшает скачки пинга при игре через Wi-Fi.", true),
        new("ad-lso", "Отключить Large Send Offload", "Пакеты формируются процессором, а не сетевой картой. Иногда убирает задержки пакетов при отправке.", true),
        new("ad-rsc", "Отключить Receive Segment Coalescing на адаптере", "Принятые пакеты не склеиваются перед передачей системе.", true),
        new("tcp-rsc", "Отключить Receive Segment Coalescing глобально", "Системная настройка RSC. Влияет на TCP-трафик, на UDP игры напрямую не влияет.", false),
        new("tcp-timestamps", "Отключить TCP timestamps", "Из TCP-заголовков убираются 12 байт. Только TCP.", false),
        new("reg-tcpack", "TCP: без задержки ACK и без алгоритма Nagle (выбранный адаптер)", "TcpAckFrequency=1, TCPNoDelay=1, TcpDelAckTicks=0. Только TCP-игры и лаунчеры, UDP не затрагивается.", true),
        new("reg-mmcss", "MMCSS: снять сетевое ограничение и повысить отзывчивость", "NetworkThrottlingIndex=отключено, SystemResponsiveness=10. Параметры из TCP Optimizer.", false),
        new("reg-games", "MMCSS: высокий приоритет задач Games", "GPU Priority 8, Priority 6, Scheduling Category High, SFIO Priority High.", false),
        new("reg-psched", "Не резервировать полосу для QoS", "NonBestEffortLimit=0. На современных Windows почти не влияет, но часто используется в профилях TCP Optimizer.", false)
    ];

    private readonly TuningBackupStore _store = new();
    private TuningBackup _backup;

    public TuningService()
    {
        _backup = _store.Load();
    }

    public bool HasBackup => _store.Exists;
    public string BackupPath => _store.Path;

    // ---------------------------------------------------------------- adapters

    public async Task<IReadOnlyList<AdapterInfo>> GetAdaptersAsync()
    {
        const string script =
            "$a=@(Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and -not $_.Virtual } | " +
            "ForEach-Object { [pscustomobject]@{ n=$_.Name; d=$_.InterfaceDescription; g=[string]$_.InterfaceGuid } }); " +
            "if ($a.Count -eq 0) { $a=@(Get-NetAdapter | Where-Object { $_.Status -eq 'Up' } | " +
            "ForEach-Object { [pscustomobject]@{ n=$_.Name; d=$_.InterfaceDescription; g=[string]$_.InterfaceGuid } }) }; " +
            "ConvertTo-Json -InputObject @($a) -Compress";
        var result = await PowerShellRunner.RunAsync(script);
        if (!result.Success || result.Output.Length == 0)
            return [];
        using var document = JsonDocument.Parse(result.Output);
        var list = new List<AdapterInfo>();
        foreach (var item in EnumerateObjects(document.RootElement))
            list.Add(new AdapterInfo(Text(item, "n"), Text(item, "d"), Text(item, "g")));
        return list;
    }

    // ---------------------------------------------------------------- apply

    public async Task<IReadOnlyList<string>> ApplyAsync(IReadOnlyCollection<string> ids, AdapterInfo? adapter)
    {
        RequireAdministrator();
        var log = new List<string>();
        var adapterTouched = false;
        foreach (var id in ids)
        {
            var definition = Tweaks.FirstOrDefault(tweak => tweak.Id == id);
            if (definition is null)
                continue;
            if (definition.NeedsAdapter && adapter is null)
            {
                log.Add($"— {definition.Title}: адаптер не выбран.");
                continue;
            }

            try
            {
                var message = id switch
                {
                    "ad-power" => await ApplyPowerAsync(adapter!),
                    "ad-irq" or "ad-eee" or "ad-roam" => await ApplyAdvancedAsync(adapter!, AdvancedTweaks[id]),
                    "ad-lso" => await ApplyLsoAsync(adapter!),
                    "ad-rsc" => await ApplyAdapterRscAsync(adapter!),
                    "tcp-rsc" => await ApplyGlobalRscAsync(),
                    "tcp-timestamps" => await ApplyTimestampsAsync(),
                    _ => ApplyRegistry(RegistrySpecs(id, adapter))
                };
                adapterTouched |= definition.NeedsAdapter && id != "reg-tcpack";
                log.Add($"✓ {definition.Title}: {message}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException or JsonException or IOException or System.ComponentModel.Win32Exception)
            {
                log.Add($"✗ {definition.Title}: {ex.Message}");
            }
        }

        _store.Save(_backup);
        if (adapterTouched && adapter is not null)
        {
            var restart = await PowerShellRunner.RunAsync($"Restart-NetAdapter -Name {PowerShellRunner.Quote(adapter.Name)}");
            log.Add(restart.Success
                ? "Адаптер перезапущен, чтобы настройки вступили в силу (соединение пропадало на несколько секунд)."
                : $"Адаптер не удалось перезапустить автоматически: {restart.Error}. Перезапустите его вручную или перезагрузите ПК.");
        }
        return log;
    }

    private static IReadOnlyList<RegistryValueSpec> RegistrySpecs(string id, AdapterInfo? adapter) => id switch
    {
        "reg-mmcss" =>
        [
            new(MultimediaPath, "NetworkThrottlingIndex", RegistryValueKind.DWord, unchecked((int)0xFFFFFFFF), 10),
            new(MultimediaPath, "SystemResponsiveness", RegistryValueKind.DWord, 10, 20)
        ],
        "reg-games" =>
        [
            new(GamesPath, "GPU Priority", RegistryValueKind.DWord, 8, 8),
            new(GamesPath, "Priority", RegistryValueKind.DWord, 6, 2),
            new(GamesPath, "Scheduling Category", RegistryValueKind.String, "High", "Medium"),
            new(GamesPath, "SFIO Priority", RegistryValueKind.String, "High", "Normal")
        ],
        "reg-psched" => [new(PschedPath, "NonBestEffortLimit", RegistryValueKind.DWord, 0, null)],
        "reg-tcpack" when adapter is not null =>
        [
            new($@"{InterfacesPath}\{adapter.Guid}", "TcpAckFrequency", RegistryValueKind.DWord, 1, null),
            new($@"{InterfacesPath}\{adapter.Guid}", "TCPNoDelay", RegistryValueKind.DWord, 1, null),
            new($@"{InterfacesPath}\{adapter.Guid}", "TcpDelAckTicks", RegistryValueKind.DWord, 0, null)
        ],
        _ => []
    };

    private string ApplyRegistry(IReadOnlyList<RegistryValueSpec> specs)
    {
        foreach (var spec in specs)
        {
            RememberRegistry(spec);
            using var key = Registry.LocalMachine.CreateSubKey(spec.Path, writable: true)
                            ?? throw new InvalidOperationException($"Не удалось открыть раздел реестра {spec.Path}.");
            key.SetValue(spec.Name, spec.Desired, spec.Kind);
        }
        return "записано в реестр.";
    }

    private void RememberRegistry(RegistryValueSpec spec)
    {
        var id = $"{spec.Path}|{spec.Name}";
        if (_backup.Registry.ContainsKey(id))
            return;
        using var key = Registry.LocalMachine.OpenSubKey(spec.Path);
        var current = key?.GetValue(spec.Name);
        _backup.Registry[id] = new RegistryBackupEntry
        {
            Path = spec.Path,
            Name = spec.Name,
            Existed = current is not null,
            Kind = current is null ? spec.Kind.ToString() : key!.GetValueKind(spec.Name).ToString(),
            Value = Convert.ToString(current, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    private AdapterBackup BackupFor(AdapterInfo adapter)
    {
        if (!_backup.Adapters.TryGetValue(adapter.Name, out var entry))
            _backup.Adapters[adapter.Name] = entry = new AdapterBackup();
        return entry;
    }

    private async Task<string> ApplyPowerAsync(AdapterInfo adapter)
    {
        var name = PowerShellRunner.Quote(adapter.Name);
        var backup = BackupFor(adapter);
        var parts = new List<string>();
        var sleep = await QueryAsync($"(Get-NetAdapterPowerManagement -Name {name} -ErrorAction SilentlyContinue).AllowComputerToTurnOffDevice");
        if (sleep is "Enabled" or "Disabled")
        {
            backup.DeviceSleep ??= sleep;
            if (sleep == "Enabled")
                await RunAsync($"Set-NetAdapterPowerManagement -Name {name} -AllowComputerToTurnOffDevice Disabled -NoRestart");
            parts.Add("питание устройства");
        }
        var advanced = await ApplyAdvancedAsync(adapter, AdvancedTweaks["ad-power"]);
        parts.Add(advanced);
        return string.Join("; ", parts);
    }

    private async Task<string> ApplyAdvancedAsync(AdapterInfo adapter, Dictionary<string, string> desired)
    {
        var props = await ReadAdvancedAsync(adapter, desired.Keys);
        if (props.Count == 0)
            return "драйвер не предоставляет эти параметры, пропущено.";
        var backup = BackupFor(adapter);
        var changed = new List<string>();
        foreach (var (keyword, prop) in props)
        {
            var wanted = desired[keyword];
            if (prop.Valid.Count == 0 || !prop.Valid.Contains(wanted))
                continue;
            backup.Advanced.TryAdd(keyword, prop.Value);
            if (prop.Value == wanted)
                continue;
            await RunAsync($"Set-NetAdapterAdvancedProperty -Name {PowerShellRunner.Quote(adapter.Name)} " +
                           $"-RegistryKeyword {PowerShellRunner.Quote(keyword)} -RegistryValue {PowerShellRunner.Quote(wanted)} -NoRestart");
            changed.Add(keyword);
        }
        return changed.Count == 0 ? "уже настроено или значение не поддерживается драйвером." : $"изменено: {string.Join(", ", changed)}.";
    }

    private async Task<string> ApplyLsoAsync(AdapterInfo adapter)
    {
        var name = PowerShellRunner.Quote(adapter.Name);
        var current = await QueryAsync($"$l=Get-NetAdapterLso -Name {name} -ErrorAction SilentlyContinue; if($l){{ \"$($l.V1IPv4Enabled),$($l.IPv4Enabled),$($l.IPv6Enabled)\" }}");
        if (string.IsNullOrEmpty(current))
            return "адаптер не поддерживает LSO, пропущено.";
        BackupFor(adapter).Lso ??= current;
        var flags = current.Split(',');
        string[] names = ["V1IPv4Enabled", "IPv4Enabled", "IPv6Enabled"];
        for (var i = 0; i < names.Length && i < flags.Length; i++)
            if (flags[i].Equals("True", StringComparison.OrdinalIgnoreCase))
                await RunAsync($"Set-NetAdapterLso -Name {name} -{names[i]} $false -NoRestart");
        return "отключено.";
    }

    private async Task<string> ApplyAdapterRscAsync(AdapterInfo adapter)
    {
        var name = PowerShellRunner.Quote(adapter.Name);
        var current = await QueryAsync($"$r=Get-NetAdapterRsc -Name {name} -ErrorAction SilentlyContinue; if($r){{ \"$($r.IPv4Enabled),$($r.IPv6Enabled)\" }}");
        if (string.IsNullOrEmpty(current))
            return "адаптер не поддерживает RSC, пропущено.";
        BackupFor(adapter).Rsc ??= current;
        var flags = current.Split(',');
        string[] names = ["IPv4Enabled", "IPv6Enabled"];
        for (var i = 0; i < names.Length && i < flags.Length; i++)
            if (flags[i].Equals("True", StringComparison.OrdinalIgnoreCase))
                await RunAsync($"Set-NetAdapterRsc -Name {name} -{names[i]} $false -NoRestart");
        return "отключено.";
    }

    private async Task<string> ApplyGlobalRscAsync()
    {
        var current = await QueryAsync("[string](Get-NetOffloadGlobalSetting).ReceiveSegmentCoalescing");
        if (current is null)
            return "не удалось прочитать текущее значение.";
        _backup.GlobalRsc ??= current;
        await RunAsync("Set-NetOffloadGlobalSetting -ReceiveSegmentCoalescing Disabled");
        return "отключено.";
    }

    private async Task<string> ApplyTimestampsAsync()
    {
        var current = await QueryAsync($"[string](Get-NetTCPSetting -SettingName {TcpSetting}).Timestamps");
        if (current is null)
            return "не удалось прочитать текущее значение.";
        _backup.Tcp.TryAdd("Timestamps", current);
        await RunAsync($"Set-NetTCPSetting -SettingName {TcpSetting} -Timestamps Disabled");
        return "отключено.";
    }

    // ---------------------------------------------------------------- MTU

    public async Task<int?> GetMtuAsync(AdapterInfo adapter)
    {
        var text = await QueryAsync($"(Get-NetIPInterface -InterfaceAlias {PowerShellRunner.Quote(adapter.Name)} -AddressFamily IPv4).NlMtu");
        return int.TryParse(text, out var mtu) ? mtu : null;
    }

    public async Task SetMtuAsync(AdapterInfo adapter, int mtu)
    {
        RequireAdministrator();
        if (mtu is < 576 or > 1500)
            throw new InvalidOperationException("MTU должен быть в диапазоне 576–1500.");
        var current = await GetMtuAsync(adapter);
        var backup = BackupFor(adapter);
        if (backup.Mtu is null && current is not null)
            backup.Mtu = current;
        _store.Save(_backup);
        await RunAsync($"Set-NetIPInterface -InterfaceAlias {PowerShellRunner.Quote(adapter.Name)} -AddressFamily IPv4 -NlMtuBytes {mtu}");
    }

    // ---------------------------------------------------------------- QoS / DSCP

    public static string QosPolicyName(string processName) => QosPolicyPrefix + processName.Trim().ToLowerInvariant();

    public async Task EnableQosAsync(string processName, int dscp)
    {
        RequireAdministrator();
        var exe = NormalizeExecutable(processName);
        if (dscp is < 0 or > 63)
            throw new InvalidOperationException("DSCP должен быть в диапазоне 0–63.");
        // Windows ignores DSCP policies on computers outside a domain unless NLA is bypassed.
        ApplyRegistryWithoutRestart(new RegistryValueSpec(QosPath, "Do not use NLA", RegistryValueKind.String, "1", null));
        var policy = QosPolicyName(Path.GetFileNameWithoutExtension(exe));
        var script =
            $"Remove-NetQosPolicy -Name {PowerShellRunner.Quote(policy)} -Confirm:$false -ErrorAction SilentlyContinue; " +
            $"New-NetQosPolicy -Name {PowerShellRunner.Quote(policy)} -AppPathNameMatchCondition {PowerShellRunner.Quote(exe)} " +
            $"-IPProtocolMatchCondition UDP -DSCPAction {dscp} -NetworkProfile All | Out-Null";
        await RunAsync(script);
        if (!_backup.QosPolicies.Contains(policy))
            _backup.QosPolicies.Add(policy);
        _store.Save(_backup);
    }

    public async Task RemoveQosAsync(string processName)
    {
        RequireAdministrator();
        var policy = QosPolicyName(Path.GetFileNameWithoutExtension(NormalizeExecutable(processName)));
        await RunAsync($"Remove-NetQosPolicy -Name {PowerShellRunner.Quote(policy)} -Confirm:$false -ErrorAction SilentlyContinue");
        _backup.QosPolicies.Remove(policy);
        _store.Save(_backup);
    }

    /// <summary>Limits the outgoing rate of background programs so they cannot fill the uplink queue (bufferbloat).</summary>
    public async Task<IReadOnlyList<string>> EnableThrottleAsync(IEnumerable<string> processNames, int megabitsPerSecond)
    {
        RequireAdministrator();
        if (megabitsPerSecond is < 1 or > 1000)
            throw new InvalidOperationException("Лимит должен быть от 1 до 1000 Мбит/с.");
        var log = new List<string>();
        ApplyRegistryWithoutRestart(new RegistryValueSpec(QosPath, "Do not use NLA", RegistryValueKind.String, "1", null));
        foreach (var name in processNames)
        {
            try
            {
                var exe = NormalizeExecutable(name);
                var policy = ThrottlePolicyPrefix + Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                await RunAsync(
                    $"Remove-NetQosPolicy -Name {PowerShellRunner.Quote(policy)} -Confirm:$false -ErrorAction SilentlyContinue; " +
                    $"New-NetQosPolicy -Name {PowerShellRunner.Quote(policy)} -AppPathNameMatchCondition {PowerShellRunner.Quote(exe)} " +
                    $"-ThrottleRateActionBitsPerSecond {megabitsPerSecond * 1_000_000L} -NetworkProfile All | Out-Null");
                if (!_backup.QosPolicies.Contains(policy))
                    _backup.QosPolicies.Add(policy);
                log.Add($"✓ {exe}: отдача не выше {megabitsPerSecond} Мбит/с.");
            }
            catch (InvalidOperationException ex)
            {
                log.Add($"✗ {name}: {ex.Message}");
            }
        }
        _store.Save(_backup);
        return log;
    }

    public async Task RemoveThrottlesAsync()
    {
        RequireAdministrator();
        await RunAsync($"Get-NetQosPolicy -ErrorAction SilentlyContinue | Where-Object {{ $_.Name -like '{ThrottlePolicyPrefix}*' }} | Remove-NetQosPolicy -Confirm:$false");
        _backup.QosPolicies.RemoveAll(policy => policy.StartsWith(ThrottlePolicyPrefix, StringComparison.Ordinal));
        _store.Save(_backup);
    }

    public async Task<IReadOnlyList<string>> ListQosPoliciesAsync()
    {
        var text = await QueryAsync(
            $"Get-NetQosPolicy -ErrorAction SilentlyContinue | Where-Object {{ $_.Name -like '{QosPolicyPrefix}*' }} | " +
            "ForEach-Object { \"$($_.Name): $($_.AppPathName) DSCP $($_.DSCPAction), лимит отдачи (бит/с) $($_.ThrottleRateAction)\" }");
        return string.IsNullOrWhiteSpace(text) ? [] : text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string NormalizeExecutable(string processName)
    {
        var name = Path.GetFileName(processName.Trim());
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("Укажите имя процесса игры, например cs2.");
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    private void ApplyRegistryWithoutRestart(RegistryValueSpec spec) => ApplyRegistry([spec]);

    // ---------------------------------------------------------------- game mode (power plan, background services)

    public async Task<string> EnterGameModeAsync(bool powerPlan, bool pauseServices)
    {
        RequireAdministrator();
        var parts = new List<string>();
        if (powerPlan)
        {
            var active = await QueryAsync("powercfg /getactivescheme");
            var guid = active is null ? null : System.Text.RegularExpressions.Regex.Match(active,
                "[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}").Value;
            if (!string.IsNullOrEmpty(guid) && !guid.Equals(HighPerformancePlan, StringComparison.OrdinalIgnoreCase))
            {
                _backup.PowerPlanGuid ??= guid;
                _store.Save(_backup);
            }
            var set = await PowerShellRunner.RunAsync($"powercfg /setactive {HighPerformancePlan}; if ($LASTEXITCODE -ne 0) {{ throw 'no plan' }}");
            if (!set.Success)
            {
                // The High performance plan is hidden on some laptops (modern standby); create it from the template.
                var duplicate = await PowerShellRunner.RunAsync(
                    $"$o = powercfg /duplicatescheme {HighPerformancePlan} | Out-String; " +
                    "$g = [regex]::Match($o,'[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}').Value; " +
                    "if (-not $g) { throw 'План не создан' }; powercfg /setactive $g");
                if (!duplicate.Success)
                    throw new InvalidOperationException($"Не удалось включить схему «Высокая производительность»: {duplicate.Error}");
            }
            parts.Add("схема питания «Высокая производительность»");
        }

        if (pauseServices)
        {
            var list = string.Join(",", PausableServices.Select(PowerShellRunner.Quote));
            var running = await QueryAsync(
                $"@({list}) | ForEach-Object {{ $s = Get-Service -Name $_ -ErrorAction SilentlyContinue; if ($s -and $s.Status -eq 'Running') {{ $_ }} }}");
            var names = string.IsNullOrWhiteSpace(running)
                ? []
                : running.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var service in names)
            {
                if (!_backup.PausedServices.Contains(service))
                    _backup.PausedServices.Add(service);
            }
            _store.Save(_backup);
            foreach (var service in names)
                await PowerShellRunner.RunAsync($"Stop-Service -Name {PowerShellRunner.Quote(service)} -Force");
            parts.Add(names.Length == 0 ? "фоновые службы уже остановлены" : $"остановлены службы: {string.Join(", ", names)}");
        }
        return string.Join("; ", parts);
    }

    public async Task<string> ExitGameModeAsync()
    {
        RequireAdministrator();
        var parts = new List<string>();
        if (_backup.PowerPlanGuid is { } plan)
        {
            var result = await PowerShellRunner.RunAsync($"powercfg /setactive {plan}; if ($LASTEXITCODE -ne 0) {{ throw 'no plan' }}");
            if (!result.Success)
                await PowerShellRunner.RunAsync($"powercfg /setactive {BalancedPlan}");
            _backup.PowerPlanGuid = null;
            parts.Add("схема питания возвращена");
        }
        if (_backup.PausedServices.Count > 0)
        {
            foreach (var service in _backup.PausedServices.ToArray())
                await PowerShellRunner.RunAsync($"Start-Service -Name {PowerShellRunner.Quote(service)}");
            parts.Add($"запущены службы: {string.Join(", ", _backup.PausedServices)}");
            _backup.PausedServices.Clear();
        }
        _store.Save(_backup);
        return parts.Count == 0 ? "ничего восстанавливать не требовалось" : string.Join("; ", parts);
    }

    // ---------------------------------------------------------------- restore

    /// <summary>Returns every setting PingBoost changed to the value it had before the first change.</summary>
    public async Task<IReadOnlyList<string>> RestorePreviousAsync()
    {
        RequireAdministrator();
        var log = new List<string>();
        _backup = _store.Load();

        foreach (var entry in _backup.Registry.Values)
        {
            try
            {
                if (entry.Existed)
                {
                    using var key = Registry.LocalMachine.CreateSubKey(entry.Path, writable: true);
                    if (entry.Kind == nameof(RegistryValueKind.String))
                        key.SetValue(entry.Name, entry.Value, RegistryValueKind.String);
                    else
                        key.SetValue(entry.Name, int.Parse(entry.Value, System.Globalization.CultureInfo.InvariantCulture), RegistryValueKind.DWord);
                }
                else
                {
                    DeleteRegistryValue(entry.Path, entry.Name);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or FormatException or IOException)
            {
                log.Add($"✗ Реестр {entry.Path}\\{entry.Name}: {ex.Message}");
            }
        }
        log.Add($"✓ Реестр: восстановлено значений — {_backup.Registry.Count}.");

        if (_backup.Tcp.TryGetValue("Timestamps", out var timestamps))
            log.Add(await TryAsync("TCP timestamps", $"Set-NetTCPSetting -SettingName {TcpSetting} -Timestamps {timestamps}"));
        if (_backup.GlobalRsc is { } rsc)
            log.Add(await TryAsync("RSC (глобально)", $"Set-NetOffloadGlobalSetting -ReceiveSegmentCoalescing {rsc}"));

        var restartNames = new List<string>();
        foreach (var (name, adapter) in _backup.Adapters)
        {
            var quoted = PowerShellRunner.Quote(name);
            foreach (var (keyword, value) in adapter.Advanced)
                log.Add(await TryAsync($"{name}: {keyword}", $"Set-NetAdapterAdvancedProperty -Name {quoted} -RegistryKeyword {PowerShellRunner.Quote(keyword)} -RegistryValue {PowerShellRunner.Quote(value)} -NoRestart"));
            if (adapter.DeviceSleep is { } sleep)
                log.Add(await TryAsync($"{name}: питание устройства", $"Set-NetAdapterPowerManagement -Name {quoted} -AllowComputerToTurnOffDevice {sleep} -NoRestart"));
            if (adapter.Lso is { } lso)
                log.Add(await TryAsync($"{name}: LSO", LsoScript(quoted, lso)));
            if (adapter.Rsc is { } adapterRsc)
                log.Add(await TryAsync($"{name}: RSC", RscScript(quoted, adapterRsc)));
            if (adapter.Mtu is { } mtu)
                log.Add(await TryAsync($"{name}: MTU", $"Set-NetIPInterface -InterfaceAlias {quoted} -AddressFamily IPv4 -NlMtuBytes {mtu}"));
            restartNames.Add(name);
        }

        foreach (var policy in _backup.QosPolicies)
            log.Add(await TryAsync($"QoS {policy}", $"Remove-NetQosPolicy -Name {PowerShellRunner.Quote(policy)} -Confirm:$false -ErrorAction SilentlyContinue"));

        log.Add($"✓ Игровой режим: {await ExitGameModeAsync()}.");
        await RestartAdaptersAsync(restartNames, log);

        _store.Delete();
        _backup = new TuningBackup();
        log.Add("Резервная копия использована и удалена.");
        return log;
    }

    /// <summary>
    /// Sets every setting managed by PingBoost to the documented Windows default, even without a backup.
    /// Settings that are absent by default are removed instead of being set to a value.
    /// </summary>
    public async Task<IReadOnlyList<string>> ResetToWindowsDefaultsAsync()
    {
        RequireAdministrator();
        var log = new List<string>();

        foreach (var id in new[] { "reg-mmcss", "reg-games", "reg-psched" })
            foreach (var spec in RegistrySpecs(id, null))
                SetDefault(spec, log);
        DeleteRegistryValue(QosPath, "Do not use NLA");
        log.Add("✓ Реестр: параметры MMCSS, Psched и QoS возвращены к стандартным.");

        using (var interfaces = Registry.LocalMachine.OpenSubKey(InterfacesPath))
        {
            foreach (var guid in interfaces?.GetSubKeyNames() ?? [])
                foreach (var name in new[] { "TcpAckFrequency", "TCPNoDelay", "TcpDelAckTicks" })
                    DeleteRegistryValue($@"{InterfacesPath}\{guid}", name);
        }
        log.Add("✓ Реестр: TcpAckFrequency / TCPNoDelay / TcpDelAckTicks удалены у всех интерфейсов.");

        log.Add(await TryAsync("TCP timestamps", $"Set-NetTCPSetting -SettingName {TcpSetting} -Timestamps Disabled"));
        log.Add(await TryAsync("RSC (глобально)", "Set-NetOffloadGlobalSetting -ReceiveSegmentCoalescing Enabled"));

        var adapters = await GetAdaptersAsync();
        var keywords = AdvancedTweaks.Values.SelectMany(map => map.Keys).Distinct().ToArray();
        foreach (var adapter in adapters)
        {
            var quoted = PowerShellRunner.Quote(adapter.Name);
            foreach (var (keyword, prop) in await ReadAdvancedAsync(adapter, keywords))
            {
                if (prop.Default.Length > 0 && prop.Default != prop.Value)
                    log.Add(await TryAsync($"{adapter.Name}: {keyword}", $"Set-NetAdapterAdvancedProperty -Name {quoted} -RegistryKeyword {PowerShellRunner.Quote(keyword)} -RegistryValue {PowerShellRunner.Quote(prop.Default)} -NoRestart"));
            }
            log.Add(await TryAsync($"{adapter.Name}: питание устройства", $"Set-NetAdapterPowerManagement -Name {quoted} -AllowComputerToTurnOffDevice Enabled -NoRestart"));
            log.Add(await TryAsync($"{adapter.Name}: LSO", $"Enable-NetAdapterLso -Name {quoted} -NoRestart"));
            log.Add(await TryAsync($"{adapter.Name}: RSC", $"Enable-NetAdapterRsc -Name {quoted} -NoRestart"));
            var mtu = _backup.Adapters.TryGetValue(adapter.Name, out var saved) ? saved.Mtu : null;
            log.Add(await TryAsync($"{adapter.Name}: MTU {mtu ?? 1500}", $"Set-NetIPInterface -InterfaceAlias {quoted} -AddressFamily IPv4 -NlMtuBytes {mtu ?? 1500}"));
        }

        log.Add(await TryAsync("QoS-политики PingBoost", $"Get-NetQosPolicy -ErrorAction SilentlyContinue | Where-Object {{ $_.Name -like '{QosPolicyPrefix}*' }} | Remove-NetQosPolicy -Confirm:$false"));
        log.Add($"✓ Игровой режим: {await ExitGameModeAsync()}.");
        await RestartAdaptersAsync(adapters.Select(adapter => adapter.Name).ToList(), log);

        _store.Delete();
        _backup = new TuningBackup();
        log.Add("Готово: управляемые PingBoost параметры вернулись к стандартным значениям Windows.");
        return log;
    }

    private void SetDefault(RegistryValueSpec spec, List<string> log)
    {
        try
        {
            if (spec.WindowsDefault is null)
            {
                DeleteRegistryValue(spec.Path, spec.Name);
                return;
            }
            using var key = Registry.LocalMachine.CreateSubKey(spec.Path, writable: true);
            key.SetValue(spec.Name, spec.WindowsDefault, spec.Kind);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            log.Add($"✗ Реестр {spec.Path}\\{spec.Name}: {ex.Message}");
        }
    }

    private static void DeleteRegistryValue(string path, string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    private static string LsoScript(string quotedName, string saved)
    {
        string[] names = ["V1IPv4Enabled", "IPv4Enabled", "IPv6Enabled"];
        var flags = saved.Split(',');
        var commands = new List<string>();
        for (var i = 0; i < names.Length && i < flags.Length; i++)
            if (flags[i].Equals("True", StringComparison.OrdinalIgnoreCase))
                commands.Add($"Set-NetAdapterLso -Name {quotedName} -{names[i]} $true -NoRestart");
        return commands.Count == 0 ? "$null" : string.Join("; ", commands);
    }

    private static string RscScript(string quotedName, string saved)
    {
        string[] names = ["IPv4Enabled", "IPv6Enabled"];
        var flags = saved.Split(',');
        var commands = new List<string>();
        for (var i = 0; i < names.Length && i < flags.Length; i++)
            if (flags[i].Equals("True", StringComparison.OrdinalIgnoreCase))
                commands.Add($"Set-NetAdapterRsc -Name {quotedName} -{names[i]} $true -NoRestart");
        return commands.Count == 0 ? "$null" : string.Join("; ", commands);
    }

    private static async Task RestartAdaptersAsync(IReadOnlyCollection<string> names, List<string> log)
    {
        foreach (var name in names)
        {
            var result = await PowerShellRunner.RunAsync($"Restart-NetAdapter -Name {PowerShellRunner.Quote(name)}");
            if (!result.Success)
                log.Add($"✗ Не удалось перезапустить адаптер {name}: {result.Error}");
        }
        if (names.Count > 0)
            log.Add("Сетевые адаптеры перезапущены, соединение могло пропасть на несколько секунд.");
    }

    // ---------------------------------------------------------------- current state

    public async Task<string> DescribeAsync(AdapterInfo? adapter)
    {
        var lines = new List<string>();
        var tcp = await QueryAsync($"\"TCP timestamps: $((Get-NetTCPSetting -SettingName {TcpSetting}).Timestamps)\"");
        lines.Add(tcp ?? "TCP timestamps: не удалось прочитать");
        var rsc = await QueryAsync("\"RSC (глобально): $((Get-NetOffloadGlobalSetting).ReceiveSegmentCoalescing)\"");
        lines.Add(rsc ?? "RSC: не удалось прочитать");

        foreach (var spec in new[] { "reg-mmcss", "reg-games", "reg-psched" }.SelectMany(id => RegistrySpecs(id, null)))
        {
            using var key = Registry.LocalMachine.OpenSubKey(spec.Path);
            lines.Add($"{spec.Name}: {FormatRegistry(key?.GetValue(spec.Name))}");
        }

        if (adapter is not null)
        {
            lines.Add($"— Адаптер {adapter.Name} —");
            var mtu = await GetMtuAsync(adapter);
            lines.Add($"MTU IPv4: {mtu?.ToString() ?? "не удалось прочитать"}");
            var sleep = await QueryAsync($"\"Разрешено отключать для экономии энергии: $((Get-NetAdapterPowerManagement -Name {PowerShellRunner.Quote(adapter.Name)} -ErrorAction SilentlyContinue).AllowComputerToTurnOffDevice)\"");
            if (!string.IsNullOrWhiteSpace(sleep))
                lines.Add(sleep);
            var keywords = AdvancedTweaks.Values.SelectMany(map => map.Keys).Distinct().ToArray();
            foreach (var (keyword, prop) in await ReadAdvancedAsync(adapter, keywords))
                lines.Add($"{keyword}: {prop.Value} (по умолчанию {prop.Default})");
            using var iface = Registry.LocalMachine.OpenSubKey($@"{InterfacesPath}\{adapter.Guid}");
            foreach (var name in new[] { "TcpAckFrequency", "TCPNoDelay", "TcpDelAckTicks" })
                lines.Add($"{name}: {FormatRegistry(iface?.GetValue(name))}");
        }

        var policies = await ListQosPoliciesAsync();
        if (policies.Count == 0)
            lines.Add("QoS-политики PingBoost: нет");
        else
            lines.AddRange(policies.Select(policy => $"QoS {policy}"));
        lines.Add(HasBackup ? $"Резервная копия: {BackupPath}" : "Резервная копия: ещё не создана (настройки не менялись)");
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatRegistry(object? value) => value switch
    {
        null => "не задано (стандарт Windows)",
        int number => unchecked((uint)number).ToString(),
        _ => value.ToString() ?? string.Empty
    };

    // ---------------------------------------------------------------- helpers

    private sealed record AdvancedProperty(string Value, string Default, IReadOnlyList<string> Valid);

    private async Task<Dictionary<string, AdvancedProperty>> ReadAdvancedAsync(AdapterInfo adapter, IEnumerable<string> keywords)
    {
        var list = string.Join(",", keywords.Select(PowerShellRunner.Quote));
        var script =
            $"$r=@(); foreach($k in @({list})) {{ $p=Get-NetAdapterAdvancedProperty -Name {PowerShellRunner.Quote(adapter.Name)} -RegistryKeyword $k -ErrorAction SilentlyContinue; " +
            "if($p){ $r += [pscustomobject]@{ k=$k; v=[string]($p.RegistryValue -join ','); d=[string]($p.DefaultRegistryValue -join ','); valid=@($p.ValidRegistryValues | ForEach-Object { [string]$_ }) } } }; " +
            "ConvertTo-Json -InputObject @($r) -Compress -Depth 4";
        var result = new Dictionary<string, AdvancedProperty>(StringComparer.OrdinalIgnoreCase);
        var output = await QueryAsync(script);
        if (string.IsNullOrWhiteSpace(output))
            return result;
        using var document = JsonDocument.Parse(output);
        foreach (var item in EnumerateObjects(document.RootElement))
        {
            var valid = new List<string>();
            if (item.TryGetProperty("valid", out var validElement))
            {
                if (validElement.ValueKind == JsonValueKind.Array)
                    valid.AddRange(validElement.EnumerateArray().Select(entry => entry.ToString()));
                else if (validElement.ValueKind == JsonValueKind.String)
                    valid.Add(validElement.GetString() ?? string.Empty);
            }
            result[Text(item, "k")] = new AdvancedProperty(Text(item, "v"), Text(item, "d"), valid);
        }
        return result;
    }

    private static IEnumerable<JsonElement> EnumerateObjects(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : [root];

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : string.Empty;

    private static async Task<string?> QueryAsync(string script)
    {
        var result = await PowerShellRunner.RunAsync(script);
        return result.Success ? result.Output : null;
    }

    private static async Task RunAsync(string script)
    {
        var result = await PowerShellRunner.RunAsync(script);
        if (!result.Success)
            throw new InvalidOperationException(result.Error.Length > 0 ? result.Error : "PowerShell завершился с ошибкой.");
    }

    private static async Task<string> TryAsync(string title, string script)
    {
        var result = await PowerShellRunner.RunAsync(script);
        return result.Success ? $"✓ {title}" : $"✗ {title}: {result.Error}";
    }

    private static void RequireAdministrator()
    {
        if (!PowerShellRunner.IsAdministrator())
            throw new UnauthorizedAccessException("Нужны права администратора. Перезапустите PingBoost от имени администратора.");
    }
}
