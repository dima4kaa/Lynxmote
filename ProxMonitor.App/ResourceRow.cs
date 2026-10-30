using CommunityToolkit.Mvvm.ComponentModel;
using ProxMonitor.Core;

namespace ProxMonitor.App;

public enum Health { Ok, Danger, Off, Unknown }

/// <summary>One table row; updated in place each poll so the grid keeps sort and selection.</summary>
public partial class ResourceRow(string key, string sourceKey, string localId) : ObservableObject
{
    const int HistoryLength = 1200; // 10 min at the demo's 0.5 s poll
    const int RecentLength = 60;

    public string Key { get; } = key;
    public string SourceKey { get; } = sourceKey;
    public string LocalId { get; } = localId;
    public bool IsGuest => !IsNode;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsGuest))] private bool _isNode;
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private bool _canStop;
    [ObservableProperty] private string _guestsText = ""; // nodes only
    [ObservableProperty] private bool _isFilterActive; // nodes only: the table shows just this node's guests
    [ObservableProperty] private string _addressText = "…"; // IP for the inspector, loaded on first selection
    public string? Address { get; set; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _kindText = "";
    [ObservableProperty] private string _node = "";
    [ObservableProperty] private bool _running;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _stale;
    [ObservableProperty] private bool _hasProblem;
    [ObservableProperty] private Health _health = Health.Unknown;
    [ObservableProperty] private string _section = "";

    [ObservableProperty] private double _cpu; // percent
    [ObservableProperty] private double _memPct;
    [ObservableProperty] private string _memText = "";
    [ObservableProperty] private double _diskPct;
    [ObservableProperty] private string _diskText = "";
    [ObservableProperty] private double? _temp;
    [ObservableProperty] private string _tempText = "";
    [ObservableProperty] private long _netTotal;
    [ObservableProperty] private string _netText = "";
    [ObservableProperty] private int? _connections;
    [ObservableProperty] private string _connectionsText = "";
    [ObservableProperty] private TimeSpan _uptime;
    [ObservableProperty] private string _uptimeText = "";

    [ObservableProperty] private double[] _cpuRecent = [];
    [ObservableProperty] private double[] _cpuHistory = [];
    [ObservableProperty] private double[] _memHistory = [];
    [ObservableProperty] private double[] _tempHistory = [];
    [ObservableProperty] private double[] _netInHistory = [];
    [ObservableProperty] private double[] _netOutHistory = [];
    [ObservableProperty] private double _netScale = 1;

    public void Update(ResourceSnapshot s, Thresholds t)
    {
        Name = s.Name;
        Node = s.Node;
        Running = s.Running;
        Stale = false;
        IsNode = s.Kind == ResourceKind.Node;
        KindText = s.Kind switch { ResourceKind.Node => L.T("Нода"), ResourceKind.Vm => "VM", _ => "LXC" };
        StatusText = L.T(s.Running ? "Работает" : "Остановлен");
        CanStart = !IsNode && !s.Running;
        CanStop = !IsNode && s.Running;

        Cpu = Math.Round(s.Cpu * 100, 1);
        MemPct = Pct(s.MemUsed, s.MemTotal);
        MemText = L.F("{0} / {1} ГБ", Gb(s.MemUsed), Gb(s.MemTotal));
        DiskPct = Pct(s.DiskUsed, s.DiskTotal);
        DiskText = L.F("{0} / {1} ГБ", Gb(s.DiskUsed), Gb(s.DiskTotal));
        Temp = s.TempC;
        TempText = s.TempC is { } c ? $"{c:0} °C" : "—";
        NetTotal = s.NetIn + s.NetOut;
        NetText = s.Running ? $"↓ {Rate(s.NetIn)}  ↑ {Rate(s.NetOut)}" : "—";
        Connections = s.Connections;
        ConnectionsText = s.Connections?.ToString() ?? "—";
        Uptime = s.Uptime;
        UptimeText = s.Running ? FormatUptime(s.Uptime) : "—";
        Health = !s.Running ? Health.Off
            : Cpu >= t.CpuPct || DiskPct >= t.DiskPct || s.TempC >= t.TempC ? Health.Danger
            : Health.Ok;
        HasProblem = Health != Health.Ok;

        CpuHistory = Append(CpuHistory, Cpu);
        CpuRecent = CpuHistory[Math.Max(0, CpuHistory.Length - RecentLength)..];
        MemHistory = Append(MemHistory, MemPct);
        if (s.TempC is { } temp) TempHistory = Append(TempHistory, temp);
        NetInHistory = Append(NetInHistory, s.NetIn);
        NetOutHistory = Append(NetOutHistory, s.NetOut);
        NetScale = Math.Max(1, Math.Max(NetInHistory.Max(), NetOutHistory.Max()));
    }

    /// <summary>Source stopped answering: keep the last values but show them as outdated.</summary>
    public void MarkStale()
    {
        Stale = true;
        StatusText = L.T("Нет данных");
        Health = Health.Unknown;
    }

    static double[] Append(double[] history, double value) =>
        history.Length < HistoryLength ? [.. history, value] : [.. history.AsSpan(1), value];

    static double Pct(long used, long total) => total == 0 ? 0 : Math.Round(100.0 * used / total, 1);

    static string Gb(long bytes) => (bytes / (double)(1L << 30)).ToString("0.#");

    static string Rate(long bytesPerSec) => bytesPerSec >= 1 << 20
        ? L.F("{0:0.0} МБ/с", bytesPerSec / (double)(1 << 20))
        : L.F("{0:0} КБ/с", bytesPerSec / 1024.0);

    static string FormatUptime(TimeSpan u) =>
        u.TotalDays >= 1 ? L.F("{0} д {1} ч", (int)u.TotalDays, u.Hours)
        : u.TotalHours >= 1 ? L.F("{0} ч {1} мин", u.Hours, u.Minutes)
        : L.F("{0} мин {1} с", u.Minutes, u.Seconds);
}
