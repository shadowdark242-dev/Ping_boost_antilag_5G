using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PingBoost.Services;

/// <summary>A row of the connection table that updates in place, so selection and sorting survive refreshes.</summary>
public sealed class NetConnectionItem(string key) : INotifyPropertyChanged
{
    private string _process = string.Empty, _protocol = string.Empty, _local = string.Empty, _remote = string.Empty;
    private string _host = string.Empty, _state = string.Empty;
    private int _pid, _connections;
    private double _downRate, _upRate;
    private long _totalDown, _totalUp, _retransmits;
    private bool _isActive;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; } = key;
    public string Process { get => _process; private set => Set(ref _process, value); }
    public int Pid { get => _pid; private set => Set(ref _pid, value); }
    public string Protocol { get => _protocol; private set => Set(ref _protocol, value); }
    public string Local { get => _local; private set => Set(ref _local, value); }
    public string Remote { get => _remote; private set => Set(ref _remote, value); }
    public string Host { get => _host; private set => Set(ref _host, value); }
    public string State { get => _state; private set => Set(ref _state, value); }
    public int Connections { get => _connections; private set => Set(ref _connections, value); }
    public bool IsActive { get => _isActive; private set => Set(ref _isActive, value); }

    public double DownRate
    {
        get => _downRate;
        private set { if (Set(ref _downRate, value)) Raise(nameof(DownText)); }
    }

    public double UpRate
    {
        get => _upRate;
        private set { if (Set(ref _upRate, value)) Raise(nameof(UpText)); }
    }

    public long TotalDown
    {
        get => _totalDown;
        private set { if (Set(ref _totalDown, value)) Raise(nameof(TotalDownText)); }
    }

    public long TotalUp
    {
        get => _totalUp;
        private set { if (Set(ref _totalUp, value)) Raise(nameof(TotalUpText)); }
    }

    public long Retransmits
    {
        get => _retransmits;
        private set { if (Set(ref _retransmits, value)) Raise(nameof(RetransmitText)); }
    }

    public string DownText => NetFormat.Rate(_downRate);
    public string UpText => NetFormat.Rate(_upRate);
    public string TotalDownText => NetFormat.Bytes(_totalDown);
    public string TotalUpText => NetFormat.Bytes(_totalUp);
    public string RetransmitText => _retransmits == 0 ? "—" : _retransmits.ToString();

    public void Update(ConnectionSnapshot snapshot)
    {
        Process = snapshot.Process;
        Pid = snapshot.ProcessId;
        Protocol = snapshot.Protocol;
        Local = snapshot.Local;
        Remote = snapshot.Remote;
        Host = snapshot.Host;
        State = snapshot.State;
        Connections = snapshot.Connections;
        DownRate = snapshot.DownBytesPerSecond;
        UpRate = snapshot.UpBytesPerSecond;
        TotalDown = snapshot.TotalDown;
        TotalUp = snapshot.TotalUp;
        Retransmits = snapshot.Retransmits;
        IsActive = snapshot.Active;
        // Units depend on the interface language, which can change between refreshes.
        Raise(nameof(DownText));
        Raise(nameof(UpText));
        Raise(nameof(TotalDownText));
        Raise(nameof(TotalUpText));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
