using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using PingBoost.Services;

namespace PingBoost;

public partial class MainWindow
{
    private static readonly string[] NetworkSortProperties =
    [
        nameof(NetConnectionItem.Process), nameof(NetConnectionItem.Pid), nameof(NetConnectionItem.Protocol),
        nameof(NetConnectionItem.Host), nameof(NetConnectionItem.Remote), nameof(NetConnectionItem.State),
        nameof(NetConnectionItem.DownRate), nameof(NetConnectionItem.UpRate), nameof(NetConnectionItem.TotalDown),
        nameof(NetConnectionItem.TotalUp), nameof(NetConnectionItem.Retransmits)
    ];

    private static readonly string[] NetworkFilterProperties =
    [
        nameof(NetConnectionItem.Process), nameof(NetConnectionItem.Pid), nameof(NetConnectionItem.Protocol),
        nameof(NetConnectionItem.Host), nameof(NetConnectionItem.Remote), nameof(NetConnectionItem.IsActive)
    ];

    private readonly NetConnectionMonitor _netMonitor = new();
    private readonly Dictionary<string, NetConnectionItem> _netConnectionIndex = [];
    private readonly Dictionary<string, NetConnectionItem> _netProcessIndex = [];
    private DispatcherTimer? _netTimer;
    private string _netFilterText = string.Empty;
    private bool _netActiveOnly;
    private bool _netByProcess;
    private bool _netReady;

    public ObservableCollection<NetConnectionItem> NetConnections { get; } = [];
    public ObservableCollection<NetConnectionItem> NetProcesses { get; } = [];

    private static bool English => NetFormat.English;

    private void InitializeNetworkTab()
    {
        ApplyNetworkLanguage();
        ConfigureNetworkView(NetConnections);
        ConfigureNetworkView(NetProcesses);
        NetworkGrid.ItemsSource = NetConnections;
        _netReady = true;
    }

    private void ApplyNetworkLanguage() => NetFormat.English = _settings.EffectiveLanguage == "en";

    private void ConfigureNetworkView(ObservableCollection<NetConnectionItem> source)
    {
        if (CollectionViewSource.GetDefaultView(source) is not ListCollectionView view)
            return;
        // Rows are updated in place, so live shaping keeps the table sorted and filtered while speeds change.
        if (view.CanChangeLiveSorting)
        {
            view.IsLiveSorting = true;
            foreach (var property in NetworkSortProperties)
                view.LiveSortingProperties.Add(property);
        }
        view.SortDescriptions.Add(new SortDescription(nameof(NetConnectionItem.DownRate), ListSortDirection.Descending));
        view.Filter = NetworkFilter;
        if (view.CanChangeLiveFiltering)
        {
            view.IsLiveFiltering = true;
            foreach (var property in NetworkFilterProperties)
                view.LiveFilteringProperties.Add(property);
        }
    }

    private bool NetworkFilter(object row)
    {
        if (row is not NetConnectionItem item)
            return false;
        if (_netActiveOnly && !item.IsActive)
            return false;
        if (_netFilterText.Length == 0)
            return true;
        return Contains(item.Process) || Contains(item.Host) || Contains(item.Remote)
               || Contains(item.Protocol) || Contains(item.Pid.ToString());

        bool Contains(string value) => value.Contains(_netFilterText, StringComparison.OrdinalIgnoreCase);
    }

    private ICollectionView CurrentNetworkView => CollectionViewSource.GetDefaultView(_netByProcess ? NetProcesses : NetConnections);

    private void NetworkTab_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_netReady)
            return;
        // Measure only while the tab is on screen: the kernel trace and DNS lookups cost CPU.
        if (e.NewValue is true)
            StartNetworkMonitor();
        else
            StopNetworkMonitor();
    }

    private void StartNetworkMonitor()
    {
        if (_netMonitor.IsRunning)
            return;
        _netMonitor.Start();
        NetStatusText.Text = NetworkStatusText();
        RefreshNetworkRows();
        _netTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _netTimer.Tick += (_, _) => RefreshNetworkRows();
        _netTimer.Start();
    }

    private void StopNetworkMonitor()
    {
        _netTimer?.Stop();
        _netTimer = null;
        _netMonitor.Stop();
        NetConnections.Clear();
        NetProcesses.Clear();
        _netConnectionIndex.Clear();
        _netProcessIndex.Clear();
    }

    private string NetworkStatusText()
    {
        if (_netMonitor.IsCapturing)
            return English
                ? "Traffic of every connection is measured (ETW). Server names come from the Windows DNS cache, otherwise from reverse DNS; some addresses have no name."
                : "Трафик каждого подключения измеряется (ETW). Имена серверов берутся из кэша DNS Windows, иначе из обратного DNS; у части адресов имени нет.";
        if (_netMonitor.CaptureProblem == "NotElevated")
            return English
                ? "Speeds are unavailable: restart PingBoost as administrator (Optimization tab). The connection list and server names work without it."
                : "Скорости недоступны: перезапустите PingBoost от администратора (вкладка «Оптимизация»). Список подключений и имена серверов работают и без этого.";
        return English
            ? $"Could not start speed measurement: {_netMonitor.CaptureProblem}. Showing the connection list without speeds."
            : $"Не удалось включить измерение скорости: {_netMonitor.CaptureProblem}. Показан список подключений без скоростей.";
    }

    private void RefreshNetworkRows()
    {
        var snapshot = _netMonitor.Snapshot();
        if (_netByProcess)
            SyncNetworkItems(NetProcesses, _netProcessIndex, GroupByProcess(snapshot));
        else
            SyncNetworkItems(NetConnections, _netConnectionIndex, snapshot);

        var down = snapshot.Sum(row => row.DownBytesPerSecond);
        var up = snapshot.Sum(row => row.UpBytesPerSecond);
        var active = snapshot.Count(row => row.Active);
        NetSummaryText.Text = English
            ? $"Total: ↓ {NetFormat.Rate(down)} · ↑ {NetFormat.Rate(up)} · connections: {snapshot.Count} (with traffic: {active})"
            : $"Всего: ↓ {NetFormat.Rate(down)} · ↑ {NetFormat.Rate(up)} · подключений: {snapshot.Count} (с трафиком: {active})";
    }

    private static void SyncNetworkItems(ObservableCollection<NetConnectionItem> target,
        Dictionary<string, NetConnectionItem> index, IReadOnlyList<ConnectionSnapshot> rows)
    {
        var seen = new HashSet<string>(rows.Count);
        foreach (var row in rows)
        {
            seen.Add(row.Key);
            if (index.TryGetValue(row.Key, out var item))
            {
                item.Update(row);
                continue;
            }
            item = new NetConnectionItem(row.Key);
            item.Update(row);
            index[row.Key] = item;
            target.Add(item);
        }
        foreach (var key in index.Keys.Where(key => !seen.Contains(key)).ToList())
        {
            target.Remove(index[key]);
            index.Remove(key);
        }
    }

    private static IReadOnlyList<ConnectionSnapshot> GroupByProcess(IReadOnlyList<ConnectionSnapshot> rows)
    {
        var label = English ? "connections" : "подключений";
        return rows.GroupBy(row => row.Process).Select(group =>
        {
            var top = group.OrderByDescending(row => row.DownBytesPerSecond + row.UpBytesPerSecond).First();
            var host = top.Host.Length > 0 ? top.Host : group.Select(row => row.Host).FirstOrDefault(name => name.Length > 0) ?? string.Empty;
            var pids = group.Select(row => row.ProcessId).Distinct().ToList();
            return new ConnectionSnapshot(
                Key: group.Key,
                Protocol: string.Join("/", group.Select(row => row.Protocol).Distinct().Order()),
                ProcessId: pids.Count == 1 ? pids[0] : 0,
                Process: group.Key,
                Local: string.Empty,
                Remote: $"{group.Count()} {label}",
                Host: host,
                State: string.Empty,
                DownBytesPerSecond: group.Sum(row => row.DownBytesPerSecond),
                UpBytesPerSecond: group.Sum(row => row.UpBytesPerSecond),
                TotalDown: group.Sum(row => row.TotalDown),
                TotalUp: group.Sum(row => row.TotalUp),
                Retransmits: group.Sum(row => row.Retransmits),
                Connections: group.Count(),
                Active: group.Any(row => row.Active));
        }).ToList();
    }

    private void NetFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_netReady)
            return;
        _netFilterText = NetFilterTextBox.Text.Trim();
        _netActiveOnly = NetActiveOnlyCheckBox.IsChecked == true;
        CurrentNetworkView.Refresh();
    }

    private void NetByProcess_Changed(object sender, RoutedEventArgs e)
    {
        if (!_netReady)
            return;
        _netByProcess = NetByProcessCheckBox.IsChecked == true;
        // The other collection holds stale rows from before the switch.
        var stale = _netByProcess ? NetProcesses : NetConnections;
        stale.Clear();
        (_netByProcess ? _netProcessIndex : _netConnectionIndex).Clear();
        NetworkGrid.ItemsSource = _netByProcess ? NetProcesses : NetConnections;
        if (_netMonitor.IsRunning)
            RefreshNetworkRows();
    }

    private void ShutdownNetworkMonitor()
    {
        _netTimer?.Stop();
        _netMonitor.Dispose();
    }
}
