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
        new("tcp-ecn", "TCP: включить ECN", "Если оператор помечает перегрузку в пакете вместо отбрасывания, TCP снижает скорость до того, как очередь вырастет. Работает только если оператор поддерживает ECN; иначе ничего не меняется. Только TCP, действует на новые соединения.", false),
        new("ad-eee", "Отключить Energy-Efficient Ethernet / Green Ethernet", "Убирает короткие «засыпания» проводного порта между пакетами.", true),
        new("ad-roam", "Минимальная агрессивность роуминга Wi-Fi", "Адаптер реже сканирует эфир и переключается между точками; уменьшает скачки пинга при игре через Wi-Fi.", true),
        new("ad-lso", "Отключить Large Send Offload", "Пакеты формируются процессором, а не сетевой картой. Иногда убирает задержки пакетов при отправке.", true),
        new("ad-rsc", "Отключить Receive Segment Coalescing на адаптере", "Принятые пакеты не склеиваются перед передачей системе.", true),
        new("tcp-rsc", "Отключить Receive Segment Coalescing глобально", "Системная настройка RSC. Влияет на TCP-трафик, на UDP игры напрямую не влияет.", false),
        new("tcp-timestamps", "Отключить TCP timestamps", "Из TCP-заголовков убираются 12 байт. Только TCP.", false),
        new("tcp-autotune-restricted", "Ограничить приём TCP: autotuning Restricted", "Окно приёма до 1 МБ: одно соединение не разгонится выше примерно 140 Мбит/с при пинге 60 мс. Скачивание меньше забивает очередь модема. Только TCP, действует на новые соединения.", false),
        new("tcp-autotune-highly", "Ограничить приём TCP сильнее: autotuning HighlyRestricted", "Окно приёма до 256 КБ: примерно 35 Мбит/с на соединение при пинге 60 мс. Заметно снижает скорость загрузок, а пинг под нагрузкой обычно падает сильнее. Только TCP. Если отмечены оба пункта, применяется этот.", false),
        new("reg-tcpack", "TCP: без задержки ACK и без алгоритма Nagle (выбранный адаптер)", "TcpAckFrequency=1, TCPNoDelay=1, TcpDelAckTicks=0. Только TCP-игры и лаунчеры, UDP не затрагивается.", true),
        new("reg-mmcss", "MMCSS: снять сетевое ограничение и повысить отзывчивость", "NetworkThrottlingIndex=отключено, SystemResponsiveness=10. Параметры из TCP Optimizer.", false),
        new("reg-games", "MMCSS: высокий приоритет задач Games", "GPU Priority 8, Priority 6, Scheduling Category High, SFIO Priority High.", false)
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

            // Even a partly failed tweak may have changed something, so the adapter is restarted afterwards.
            adapterTouched |= definition.NeedsAdapter && id != "reg-tcpack";
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
                    "tcp-autotune-restricted" => await ApplyAutoTuningAsync("Restricted"),
                    "tcp-autotune-highly" => await ApplyAutoTuningAsync("HighlyRestricted"),
                    "tcp-ecn" => await ApplyEcnAsync(),
                    _ => ApplyRegistry(RegistrySpecs(id, adapter))
                };
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

    /// <summary>Clears only the power-off bits PingBoost sets, leaving other PnPCapabilities bits of the driver intact.</summary>
    private static string ResetDeviceSleep(AdapterInfo adapter)
    {
        try
        {
            var classKey = FindAdapterClassKey(adapter.Guid);
            if (classKey is null)
                return $"— {adapter.Name}: питание устройства: раздел драйвера не найден";
            using var key = Registry.LocalMachine.OpenSubKey(classKey, writable: true);
            if (key?.GetValue("PnPCapabilities") is not int current || (current & PnpDisableDeviceSleep) != PnpDisableDeviceSleep)
                return $"✓ {adapter.Name}: питание устройства (запрет не был установлен)";
            var cleared = current & ~PnpDisableDeviceSleep;
            if (cleared == 0)
                key.DeleteValue("PnPCapabilities", throwOnMissingValue: false);
            else
                key.SetValue("PnPCapabilities", cleared, RegistryValueKind.DWord);
            return $"✓ {adapter.Name}: питание устройства (PnPCapabilities {current} → {(cleared == 0 ? "не задано" : cleared)})";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return $"✗ {adapter.Name}: питание устройства: {ex.Message}";
        }
    }

    private const string NetClassPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
    private const int PnpDisableDeviceSleep = 0x18;

    private static string? FindAdapterClassKey(string interfaceGuid)
    {
        using var root = Registry.LocalMachine.OpenSubKey(NetClassPath);
        if (root is null)
            return null;
        foreach (var name in root.GetSubKeyNames())
        {
            if (!int.TryParse(name, out _))
                continue;
            using var key = root.OpenSubKey(name);
            if (string.Equals(key?.GetValue("NetCfgInstanceId") as string, interfaceGuid, StringComparison.OrdinalIgnoreCase))
                return $@"{NetClassPath}\{name}";
        }
        return null;
    }

    private async Task<string> ApplyPowerAsync(AdapterInfo adapter)
    {
        var parts = new List<string>();
        // "Allow the computer to turn off this device to save power" has no cmdlet parameter; Device Manager
        // stores it in the PnPCapabilities bit mask (0x18) of the adapter's driver key.
        var classKey = FindAdapterClassKey(adapter.Guid);
        if (classKey is null)
        {
            parts.Add("питание устройства: раздел драйвера адаптера не найден, пропущено");
        }
        else
        {
            using var key = Registry.LocalMachine.OpenSubKey(classKey);
            var current = key?.GetValue("PnPCapabilities") is int existing ? existing : 0;
            var desired = current | PnpDisableDeviceSleep;
            if (desired == current)
            {
                parts.Add("питание устройства: уже запрещено выключать");
            }
            else
            {
                ApplyRegistry([new RegistryValueSpec(classKey, "PnPCapabilities", RegistryValueKind.DWord, desired, null)]);
                parts.Add($"питание устройства: PnPCapabilities {current} → {desired} (вступит в силу после перезапуска адаптера)");
            }
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
        var notes = new List<string>();
        var changed = new List<string>();
        foreach (var (keyword, prop) in props)
        {
            var wanted = desired[keyword];
            if (prop.Value.Contains(','))
            {
                notes.Add($"{keyword}: несколько значений ({prop.Value}), не трогаю");
                continue;
            }
            if (prop.Valid.Count == 0 || !prop.Valid.Contains(wanted))
            {
                notes.Add($"{keyword}: значение {wanted} не поддерживается (допустимо: {(prop.Valid.Count == 0 ? "список пуст" : string.Join("/", prop.Valid))}), пропущено");
                continue;
            }
            backup.Advanced.TryAdd(keyword, prop.Value);
            if (prop.Value == wanted)
            {
                notes.Add($"{keyword}: уже {wanted}");
                continue;
            }
            try
            {
                await RunAsync(SetAdvancedScript(adapter.Name, keyword, wanted));
                changed.Add(keyword);
            }
            catch (InvalidOperationException ex)
            {
                notes.Add($"{keyword}: не удалось записать ({ex.Message})");
            }
        }

        if (changed.Count > 0)
        {
            var verified = await ReadAdvancedAsync(adapter, changed);
            foreach (var keyword in changed)
            {
                var before = props[keyword].Value;
                var after = verified.TryGetValue(keyword, out var current) ? current.Value : "?";
                notes.Add(after == desired[keyword]
                    ? $"{keyword}: {before} → {after}"
                    : $"{keyword}: после записи значение {after}, драйвер не принял изменение");
            }
        }
        return string.Join("; ", notes) + ".";
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

    private async Task<string> ApplyEcnAsync()
    {
        var current = await QueryAsync($"[string](Get-NetTCPSetting -SettingName {TcpSetting}).EcnCapability");
        if (current is null)
            return "не удалось прочитать текущее значение.";
        _backup.Tcp.TryAdd("EcnCapability", current);
        if (current == "Enabled")
            return "уже включён.";
        await RunAsync($"Set-NetTCPSetting -SettingName {TcpSetting} -EcnCapability Enabled");
        return $"{current} → Enabled (новые соединения).";
    }

    private async Task<string> ApplyAutoTuningAsync(string level)
    {
        var current = await QueryAsync($"[string](Get-NetTCPSetting -SettingName {TcpSetting}).AutoTuningLevelLocal");
        if (current is null)
            return "не удалось прочитать текущее значение.";
        _backup.Tcp.TryAdd("AutoTuningLevelLocal", current);
        await RunAsync($"Set-NetTCPSetting -SettingName {TcpSetting} -AutoTuningLevelLocal {level}");
        return $"{current} → {level} (новые соединения; открытые загрузки продолжат со старым окном).";
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

    /// <summary>
    /// Caps the outgoing rate of everything this PC sends. A default policy only applies to traffic that no more
    /// specific policy (such as the game's DSCP policy) matches, and game traffic stays far below the cap anyway.
    /// </summary>
    public async Task<string> EnableGlobalThrottleAsync(int megabitsPerSecond)
    {
        RequireAdministrator();
        if (megabitsPerSecond is < 1 or > 1000)
            throw new InvalidOperationException("Лимит должен быть от 1 до 1000 Мбит/с.");
        ApplyRegistryWithoutRestart(new RegistryValueSpec(QosPath, "Do not use NLA", RegistryValueKind.String, "1", null));
        var policy = ThrottlePolicyPrefix + "_all";
        await RunAsync(
            $"Remove-NetQosPolicy -Name {PowerShellRunner.Quote(policy)} -Confirm:$false -ErrorAction SilentlyContinue; " +
            $"New-NetQosPolicy -Name {PowerShellRunner.Quote(policy)} -Default " +
            $"-ThrottleRateActionBitsPerSecond {megabitsPerSecond * 1_000_000L} -NetworkProfile All | Out-Null");
        if (!_backup.QosPolicies.Contains(policy))
            _backup.QosPolicies.Add(policy);
        _store.Save(_backup);
        return $"общий потолок отдачи ПК: {megabitsPerSecond} Мбит/с.";
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
            var wireless = await MaximizeWirelessPerformanceAsync();
            parts.Add("схема питания «Высокая производительность»" + (wireless is null ? string.Empty : $" ({wireless})"));
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

    private const string WirelessSubgroup = "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1";
    private const string WirelessSaving = "12bbebe6-58d6-4636-95bb-3217ef867c1a";

    /// <summary>
    /// Sets "Wireless Adapter Settings → Power Saving Mode" of the active scheme to Maximum Performance (0),
    /// remembering the original AC/DC values for <see cref="ExitGameModeAsync"/>.
    /// </summary>
    private async Task<string?> MaximizeWirelessPerformanceAsync()
    {
        var active = await QueryAsync("powercfg /getactivescheme");
        var scheme = active is null ? null : System.Text.RegularExpressions.Regex.Match(active,
            "[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}").Value;
        if (string.IsNullOrEmpty(scheme))
            return null;
        var query = await QueryAsync($"powercfg /query {scheme} {WirelessSubgroup} {WirelessSaving}");
        // The last two 0x values are the current AC and DC indexes; the possible-setting indexes are printed without the 0x prefix.
        var values = query is null
            ? []
            : System.Text.RegularExpressions.Regex.Matches(query, "0x[0-9a-fA-F]{8}").Select(match => match.Value).ToArray();
        if (values.Length < 2)
            return "параметр энергосбережения Wi-Fi недоступен в этой схеме";
        var ac = Convert.ToInt32(values[^2], 16);
        var dc = Convert.ToInt32(values[^1], 16);
        if (ac == 0 && dc == 0)
            return "энергосбережение Wi-Fi уже отключено";

        if (_backup.WirelessPlanGuid is null)
        {
            _backup.WirelessPlanGuid = scheme;
            _backup.WirelessAc = ac;
            _backup.WirelessDc = dc;
            _store.Save(_backup);
        }
        await RunAsync(
            $"powercfg /setacvalueindex {scheme} {WirelessSubgroup} {WirelessSaving} 0; " +
            $"powercfg /setdcvalueindex {scheme} {WirelessSubgroup} {WirelessSaving} 0; " +
            $"powercfg /setactive {scheme}; if ($LASTEXITCODE -ne 0) {{ throw 'powercfg не применил параметр' }}");
        return $"энергосбережение Wi-Fi: от сети {ac} → 0, от батареи {dc} → 0";
    }

    public async Task<string> ExitGameModeAsync()
    {
        RequireAdministrator();
        var parts = new List<string>();
        if (_backup.WirelessPlanGuid is { } wirelessPlan && _backup.WirelessAc is { } wirelessAc && _backup.WirelessDc is { } wirelessDc)
        {
            var restored = await PowerShellRunner.RunAsync(
                $"powercfg /setacvalueindex {wirelessPlan} {WirelessSubgroup} {WirelessSaving} {wirelessAc}; " +
                $"powercfg /setdcvalueindex {wirelessPlan} {WirelessSubgroup} {WirelessSaving} {wirelessDc}; " +
                "if ($LASTEXITCODE -ne 0) { throw 'powercfg' }");
            if (restored.Success)
            {
                _backup.WirelessPlanGuid = null;
                _backup.WirelessAc = null;
                _backup.WirelessDc = null;
                parts.Add("энергосбережение беспроводного адаптера возвращено");
            }
            else
            {
                parts.Add($"не удалось вернуть энергосбережение беспроводного адаптера: {restored.Error}");
            }
        }
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
        if (_backup.Tcp.TryGetValue("EcnCapability", out var ecn))
            log.Add(await TryAsync("TCP ECN", $"Set-NetTCPSetting -SettingName {TcpSetting} -EcnCapability {ecn}"));
        if (_backup.Tcp.TryGetValue("AutoTuningLevelLocal", out var autoTuning))
            log.Add(await TryAsync("TCP autotuning", $"Set-NetTCPSetting -SettingName {TcpSetting} -AutoTuningLevelLocal {autoTuning}"));
        if (_backup.GlobalRsc is { } rsc)
            log.Add(await TryAsync("RSC (глобально)", $"Set-NetOffloadGlobalSetting -ReceiveSegmentCoalescing {rsc}"));

        var restartNames = new List<string>();
        foreach (var (name, adapter) in _backup.Adapters)
        {
            var quoted = PowerShellRunner.Quote(name);
            foreach (var (keyword, savedValue) in adapter.Advanced)
            {
                var value = savedValue;
                if (value.Contains(','))
                {
                    // Backups made by an earlier build can hold several joined values (wildcard match); use the driver default for the exact keyword.
                    var current = await ReadAdvancedAsync(new AdapterInfo(name, string.Empty, string.Empty), [keyword]);
                    value = current.TryGetValue(keyword, out var property) ? property.Default : string.Empty;
                    if (value.Length == 0 || value.Contains(','))
                    {
                        log.Add($"✗ {name}: {keyword}: не удалось определить исходное значение ({savedValue}), проверьте параметр вручную");
                        continue;
                    }
                    log.Add($"— {name}: {keyword}: в копии было «{savedValue}», возвращаю значение драйвера по умолчанию «{value}»");
                }
                log.Add(await TryAsync($"{name}: {keyword}", SetAdvancedScript(name, keyword, value)));
            }
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

        foreach (var id in new[] { "reg-mmcss", "reg-games" })
            foreach (var spec in RegistrySpecs(id, null))
                SetDefault(spec, log);
        DeleteRegistryValue(QosPath, "Do not use NLA");
        // The "do not reserve QoS bandwidth" tweak was removed; clean up the value if an earlier build wrote it.
        using (var psched = Registry.LocalMachine.OpenSubKey(PschedPath, writable: true))
        {
            if (psched?.GetValue("NonBestEffortLimit") is 0)
                psched.DeleteValue("NonBestEffortLimit", throwOnMissingValue: false);
        }
        log.Add("✓ Реестр: параметры MMCSS, Psched и QoS возвращены к стандартным.");

        using (var interfaces = Registry.LocalMachine.OpenSubKey(InterfacesPath))
        {
            foreach (var guid in interfaces?.GetSubKeyNames() ?? [])
                foreach (var name in new[] { "TcpAckFrequency", "TCPNoDelay", "TcpDelAckTicks" })
                    DeleteRegistryValue($@"{InterfacesPath}\{guid}", name);
        }
        log.Add("✓ Реестр: TcpAckFrequency / TCPNoDelay / TcpDelAckTicks удалены у всех интерфейсов.");

        log.Add(await TryAsync("TCP timestamps", $"Set-NetTCPSetting -SettingName {TcpSetting} -Timestamps Disabled"));
        log.Add(await TryAsync("TCP ECN (Disabled)", $"Set-NetTCPSetting -SettingName {TcpSetting} -EcnCapability Disabled"));
        log.Add(await TryAsync("TCP autotuning (Normal)", $"Set-NetTCPSetting -SettingName {TcpSetting} -AutoTuningLevelLocal Normal"));
        log.Add(await TryAsync("RSC (глобально)", "Set-NetOffloadGlobalSetting -ReceiveSegmentCoalescing Enabled"));

        var adapters = await GetAdaptersAsync();
        var keywords = AdvancedTweaks.Values.SelectMany(map => map.Keys).Distinct().ToArray();
        foreach (var adapter in adapters)
        {
            var quoted = PowerShellRunner.Quote(adapter.Name);
            foreach (var (keyword, prop) in await ReadAdvancedAsync(adapter, keywords))
            {
                if (prop.Default.Length > 0 && prop.Default != prop.Value && !prop.Value.Contains(',') && !prop.Default.Contains(','))
                    log.Add(await TryAsync($"{adapter.Name}: {keyword}", SetAdvancedScript(adapter.Name, keyword, prop.Default)));
            }
            log.Add(ResetDeviceSleep(adapter));
            // Not every adapter supports LSO/RSC; enabling is attempted only where the feature exists.
            log.Add(await TryAsync($"{adapter.Name}: LSO", $"if (Get-NetAdapterLso -Name {quoted} -ErrorAction SilentlyContinue) {{ Enable-NetAdapterLso -Name {quoted} -NoRestart }}"));
            log.Add(await TryAsync($"{adapter.Name}: RSC", $"if (Get-NetAdapterRsc -Name {quoted} -ErrorAction SilentlyContinue) {{ Enable-NetAdapterRsc -Name {quoted} -NoRestart }}"));
            // The default MTU depends on the adapter (cellular modems are often below 1500), so it is only restored from a backup.
            if (_backup.Adapters.TryGetValue(adapter.Name, out var saved) && saved.Mtu is { } savedMtu)
                log.Add(await TryAsync($"{adapter.Name}: MTU {savedMtu}", $"Set-NetIPInterface -InterfaceAlias {quoted} -AddressFamily IPv4 -NlMtuBytes {savedMtu}"));
            else
                log.Add($"— {adapter.Name}: MTU не менялся через PingBoost, оставлен как есть.");
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

    /// <summary>Tells for every tweak whether its target value is currently in effect (used to tick the checkboxes).</summary>
    public async Task<IReadOnlyDictionary<string, bool>> GetAppliedTweaksAsync(AdapterInfo? adapter)
    {
        var applied = Tweaks.ToDictionary(tweak => tweak.Id, _ => false);

        var adapterPart = string.Empty;
        if (adapter is not null)
        {
            var name = PowerShellRunner.Quote(adapter.Name);
            adapterPart =
                $"$l=Get-NetAdapterLso -Name {name} -ErrorAction SilentlyContinue; if($l){{ $lso=\"$($l.V1IPv4Enabled),$($l.IPv4Enabled),$($l.IPv6Enabled)\" }}; " +
                $"$r=Get-NetAdapterRsc -Name {name} -ErrorAction SilentlyContinue; if($r){{ $rsc=\"$($r.IPv4Enabled),$($r.IPv6Enabled)\" }}; ";
        }
        var output = await QueryAsync(
            $"$t=Get-NetTCPSetting -SettingName {TcpSetting}; $g=[string](Get-NetOffloadGlobalSetting).ReceiveSegmentCoalescing; $lso=''; $rsc=''; " +
            adapterPart +
            "\"$($t.Timestamps)|$($t.EcnCapability)|$($t.AutoTuningLevelLocal)|$g|$lso|$rsc\"");
        var parts = (output ?? string.Empty).Split('|');
        if (parts.Length == 6)
        {
            applied["tcp-timestamps"] = parts[0] == "Disabled";
            applied["tcp-ecn"] = parts[1] == "Enabled";
            applied["tcp-autotune-restricted"] = parts[2] == "Restricted";
            applied["tcp-autotune-highly"] = parts[2] == "HighlyRestricted";
            applied["tcp-rsc"] = parts[3] == "Disabled";
            applied["ad-lso"] = AllFlagsOff(parts[4]);
            applied["ad-rsc"] = AllFlagsOff(parts[5]);
        }

        applied["reg-mmcss"] = RegistryApplied(RegistrySpecs("reg-mmcss", adapter));
        applied["reg-games"] = RegistryApplied(RegistrySpecs("reg-games", adapter));
        if (adapter is not null)
        {
            applied["reg-tcpack"] = RegistryApplied(RegistrySpecs("reg-tcpack", adapter));

            var props = await ReadAdvancedAsync(adapter, AdvancedTweaks.Values.SelectMany(map => map.Keys).Distinct());
            foreach (var (id, desired) in AdvancedTweaks)
                applied[id] = AdvancedApplied(props, desired);

            var classKey = FindAdapterClassKey(adapter.Guid);
            if (classKey is not null)
            {
                using var key = Registry.LocalMachine.OpenSubKey(classKey);
                var powerOff = key?.GetValue("PnPCapabilities") is int capabilities && (capabilities & PnpDisableDeviceSleep) == PnpDisableDeviceSleep;
                // The power tweak consists of the registry bits and the driver keywords, so both have to be in place.
                applied["ad-power"] = powerOff && (AdvancedApplied(props, AdvancedTweaks["ad-power"]) || !props.Keys.Any(AdvancedTweaks["ad-power"].ContainsKey));
            }
        }
        return applied;
    }

    private static bool AllFlagsOff(string flags) =>
        flags.Length > 0 && !flags.Split(',').Any(flag => flag.Equals("True", StringComparison.OrdinalIgnoreCase));

    private static bool RegistryApplied(IReadOnlyList<RegistryValueSpec> specs) =>
        specs.Count > 0 && specs.All(spec =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(spec.Path);
            return key?.GetValue(spec.Name) switch
            {
                int number when spec.Desired is int wanted => number == wanted,
                string text when spec.Desired is string wanted => text == wanted,
                _ => false
            };
        });

    /// <summary>True when at least one keyword of the tweak exists and every existing, supported keyword has the wanted value.</summary>
    private static bool AdvancedApplied(Dictionary<string, AdvancedProperty> props, Dictionary<string, string> desired)
    {
        var relevant = desired
            .Where(pair => props.TryGetValue(pair.Key, out var prop) && !prop.Value.Contains(',') && prop.Valid.Contains(pair.Value))
            .ToArray();
        return relevant.Length > 0 && relevant.All(pair => props[pair.Key].Value == pair.Value);
    }

    public async Task<string> DescribeAsync(AdapterInfo? adapter)
    {
        var lines = new List<string>();
        var tcp = await QueryAsync($"\"TCP timestamps: $((Get-NetTCPSetting -SettingName {TcpSetting}).Timestamps)\"");
        lines.Add(tcp ?? "TCP timestamps: не удалось прочитать");
        var autoTuning = await QueryAsync($"\"TCP autotuning: $((Get-NetTCPSetting -SettingName {TcpSetting}).AutoTuningLevelLocal)\"");
        lines.Add(autoTuning ?? "TCP autotuning: не удалось прочитать");
        var ecnState = await QueryAsync($"\"TCP ECN: $((Get-NetTCPSetting -SettingName {TcpSetting}).EcnCapability)\"");
        lines.Add(ecnState ?? "TCP ECN: не удалось прочитать");
        var rsc = await QueryAsync("\"RSC (глобально): $((Get-NetOffloadGlobalSetting).ReceiveSegmentCoalescing)\"");
        lines.Add(rsc ?? "RSC: не удалось прочитать");

        foreach (var spec in new[] { "reg-mmcss", "reg-games" }.SelectMany(id => RegistrySpecs(id, null)))
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
            var classKey = FindAdapterClassKey(adapter.Guid);
            if (classKey is not null)
            {
                using var driverKey = Registry.LocalMachine.OpenSubKey(classKey);
                lines.Add($"PnPCapabilities: {FormatRegistry(driverKey?.GetValue("PnPCapabilities"))} (биты 0x18 = запрет выключать устройство)");
            }
            var keywords = AdvancedTweaks.Values.SelectMany(map => map.Keys).Distinct().ToArray();
            foreach (var (keyword, prop) in await ReadAdvancedAsync(adapter, keywords))
                lines.Add($"{keyword}: {prop.Value} (по умолчанию {prop.Default}; допустимо: {(prop.Valid.Count == 0 ? "—" : string.Join("/", prop.Valid))}; строк {prop.Rows}, значений {prop.Values})");
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

    private sealed record AdvancedProperty(string Value, string Default, IReadOnlyList<string> Valid, int Rows, int Values);

    /// <summary>
    /// Writes one adapter property. The exact object is selected first (the keyword is a wildcard pattern for Get,
    /// but Set-NetAdapterAdvancedProperty compares it literally) and passed down the pipeline.
    /// </summary>
    private static string SetAdvancedScript(string adapterName, string keyword, string value) =>
        $"$k={PowerShellRunner.Quote(keyword)}; " +
        $"$o=@(Get-NetAdapterAdvancedProperty -Name {PowerShellRunner.Quote(adapterName)} -RegistryKeyword ($k.Replace('*','`*')) -ErrorAction SilentlyContinue | Where-Object {{ $_.RegistryKeyword -eq $k }}); " +
        "if ($o.Count -ne 1) { throw \"Параметр $k найден строк: $($o.Count)\" }; " +
        $"$o | Set-NetAdapterAdvancedProperty -RegistryValue {PowerShellRunner.Quote(value)} -NoRestart";

    private async Task<Dictionary<string, AdvancedProperty>> ReadAdvancedAsync(AdapterInfo adapter, IEnumerable<string> keywords)
    {
        var list = string.Join(",", keywords.Select(PowerShellRunner.Quote));
        var script =
            // -RegistryKeyword is a wildcard pattern: "*EEE" would also match "AdvancedEEE", so the star is escaped and the match re-checked exactly.
            $"$r=@(); foreach($k in @({list})) {{ $kp=$k.Replace('*','`*'); $p=Get-NetAdapterAdvancedProperty -Name {PowerShellRunner.Quote(adapter.Name)} -RegistryKeyword $kp -ErrorAction SilentlyContinue | Where-Object {{ $_.RegistryKeyword -eq $k }}; " +
            "if($p){ $r += [pscustomobject]@{ k=$k; rows=@($p).Count; n=@($p.RegistryValue).Count; v=[string]($p.RegistryValue -join ','); d=[string]($p.DefaultRegistryValue -join ','); valid=@($p.ValidRegistryValues | ForEach-Object { [string]$_ }) } } }; " +
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
            _ = int.TryParse(Text(item, "rows"), out var rows);
            _ = int.TryParse(Text(item, "n"), out var values);
            result[Text(item, "k")] = new AdvancedProperty(Text(item, "v"), Text(item, "d"), valid, rows, values);
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
