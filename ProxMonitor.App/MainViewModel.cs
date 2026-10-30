using System.Collections.ObjectModel;
using Avalonia.Collections;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxMonitor.Core;

namespace ProxMonitor.App;

/// <summary>A sheet tab at the bottom of the table. "Все" shows everything; others show what was moved there.</summary>
public partial class SheetTab(string name, bool isAll = false) : ObservableObject
{
    public string Name { get; } = name;
    public bool IsAll { get; } = isAll;
    [ObservableProperty] private int _count;
}

/// <summary>A connected server (or the demo) with its own alert state.</summary>
public partial class SourceState(IResourceSource source, Func<string, Thresholds> thresholdsFor) : ObservableObject
{
    public IResourceSource Source { get; } = source;
    public AlertEngine Engine { get; } = new(id => thresholdsFor(source.Key + ":" + id));
    public string Title => Source.Title;
    public DateTime LastPoll { get; set; }
    public bool Busy { get; set; }
    [ObservableProperty] private bool _online = true;
}

/// <summary>One line of a multi-server widget: who, a short value, and a 0..100 bar.</summary>
public sealed record WidgetLine(ResourceRow Row, string Value, double Pct);

/// <summary>A floating card over the table; Row is null until its resource has loaded.</summary>
public partial class WidgetVm(WidgetConfig config) : ObservableObject
{
    public WidgetConfig Config { get; } = config;
    public bool IsServer => Config.Kind == WidgetKind.Server;
    public bool IsGroup => Config.Kind == WidgetKind.Group;
    public bool IsSummary => Config.Kind == WidgetKind.Summary;
    public bool IsTop => Config.Kind == WidgetKind.Top;
    public bool IsCommand => Config.Kind == WidgetKind.Command;
    public bool IsList => IsGroup || IsTop;
    /// <summary>The user's name, or the kind's name in the current language.</summary>
    public string Title => Config.Title.Length > 0 ? Config.Title : L.T(KindName(Config.Kind));

    public static string KindName(string kind) => kind switch
    {
        WidgetKind.Group => "Группа серверов",
        WidgetKind.Summary => "Сводка по выборке",
        WidgetKind.Top => "Самые нагруженные",
        WidgetKind.Command => "Быстрая команда",
        _ => "Виджеты",
    };

    /// <summary>What the widget watches, for the widget builder's list.</summary>
    public string ScopeText => IsServer || IsCommand ? Row?.Name ?? "—"
        : Config.Keys.Count == 0 ? L.T("Все VM и контейнеры")
        : L.F("Серверов: {0}", Config.Keys.Count);

    [ObservableProperty] private ResourceRow? _row;
    [ObservableProperty] private IReadOnlyList<WidgetLine> _lines = [];
    [ObservableProperty] private string _summaryRunning = "—";
    [ObservableProperty] private string _summaryCpu = "—";
    [ObservableProperty] private string _summaryTemp = "—";
    [ObservableProperty] private int _summaryProblems;
    [ObservableProperty] private string _commandOutput = "";
    [ObservableProperty] private string _commandStatus = "";
    [ObservableProperty] private bool _commandBusy;

    /// <summary>Recomputes what the widget shows from the current rows.</summary>
    public void Refresh(IReadOnlyDictionary<string, ResourceRow> rows)
    {
        if (IsServer)
        {
            Row = rows.GetValueOrDefault(Config.Key);
            return;
        }
        // An empty selection means every VM and container.
        var pool = Config.Keys.Count == 0
            ? rows.Values.Where(r => !r.IsNode).ToList()
            : Config.Keys.Select(rows.GetValueOrDefault).OfType<ResourceRow>().ToList();
        Row = pool.FirstOrDefault();
        if (IsGroup)
        {
            Lines = pool.Select(r => new WidgetLine(r, r.Running ? L.F("CPU {0:0}%  RAM {1:0}%", r.Cpu, r.MemPct) : r.StatusText, r.Cpu)).ToList();
        }
        else if (IsTop)
        {
            Func<ResourceRow, double> metric = Config.Metric switch
            {
                "ram" => r => r.MemPct,
                "disk" => r => r.DiskPct,
                "temp" => r => r.Temp ?? 0,
                _ => r => r.Cpu,
            };
            var unit = Config.Metric == "temp" ? " °C" : "%";
            Lines = pool.Where(r => r.Running).OrderByDescending(metric).Take(Math.Max(1, Config.Count))
                .Select(r => new WidgetLine(r, $"{metric(r):0}{unit}", Math.Min(100, metric(r)))).ToList();
        }
        else if (IsSummary)
        {
            var running = pool.Where(r => r.Running && !r.Stale).ToList();
            SummaryRunning = $"{running.Count}/{pool.Count}";
            SummaryCpu = running.Count == 0 ? "—" : $"{running.Average(r => r.Cpu):0}%";
            // Only nodes have sensors: take the nodes the selection runs on.
            var temps = rows.Values
                .Where(n => n.IsNode && n.Temp is not null && pool.Any(r => r.SourceKey == n.SourceKey && r.Node == n.Node))
                .Select(n => n.Temp!.Value).ToList();
            SummaryTemp = temps.Count == 0 ? "—" : $"{temps.Max():0} °C";
            SummaryProblems = pool.Count(r => r.HasProblem);
        }
    }
    [ObservableProperty] private double _x = config.X;
    [ObservableProperty] private double _y = config.Y;
    [ObservableProperty] private bool _cpu = config.Cpu;
    [ObservableProperty] private bool _ram = config.Ram;
    [ObservableProperty] private bool _disk = config.Disk;
    [ObservableProperty] private bool _temp = config.Temp;
    [ObservableProperty] private bool _net = config.Net;

    partial void OnXChanged(double value) => Config.X = value;
    partial void OnYChanged(double value) => Config.Y = value;
    partial void OnCpuChanged(bool value) => Config.Cpu = value;
    partial void OnRamChanged(bool value) => Config.Ram = value;
    partial void OnDiskChanged(bool value) => Config.Disk = value;
    partial void OnTempChanged(bool value) => Config.Temp = value;
    partial void OnNetChanged(bool value) => Config.Net = value;
}

public partial class MainViewModel : ObservableObject
{
    const int MaxEvents = 300;
    public static string NoSection => L.T("Без раздела");

    readonly AppSettings _settings;
    readonly Dictionary<string, ResourceRow> _rows = [];

    public MainViewModel(AppSettings? settings = null)
    {
        _settings = settings ?? AppSettings.Load();
        View = new DataGridCollectionView(Rows) { Filter = o => Matches((ResourceRow)o) };
        Tabs.Add(new SheetTab(L.T("Все"), isAll: true));
        foreach (var name in _settings.Tabs) Tabs.Add(new SheetTab(name));
        _selectedTab = Tabs[0];
        foreach (var w in _settings.Widgets) Widgets.Add(new WidgetVm(w));
        UpdateGrouping();

        if (_settings.Demo) Sources.Add(NewSource(new DemoSource()));
        foreach (var cfg in _settings.Servers)
            Sources.Add(NewSource(new ProxmoxSource(cfg, Secrets.Unprotect(cfg.ProtectedSecret) ?? "")));
        Sources.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSources));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => PollDue();
        timer.Start();
    }

    public ObservableCollection<ResourceRow> Rows { get; } = [];
    /// <summary>Physical hosts, shown as cards above the table; the table itself lists only VMs and containers.</summary>
    public ObservableCollection<ResourceRow> Nodes { get; } = [];
    public DataGridCollectionView View { get; }
    public ObservableCollection<AlertEvent> Events { get; } = [];
    public ObservableCollection<SheetTab> Tabs { get; } = [];
    public ObservableCollection<SourceState> Sources { get; } = [];
    public ObservableCollection<WidgetVm> Widgets { get; } = [];
    public IReadOnlyList<string> Sections => _settings.Sections;
    public bool HasSources => Sources.Count > 0;
    public string Theme => _settings.Theme;

    public string Language
    {
        get => _settings.Language;
        set
        {
            _settings.Language = value;
            _settings.Save();
        }
    }

    public bool NodesCollapsed
    {
        get => _settings.NodesCollapsed;
        set
        {
            _settings.NodesCollapsed = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Raised for every new event; the window turns warnings into toasts.</summary>
    public event Action<AlertEvent>? Alert;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _onlyProblems;
    [ObservableProperty] private bool _isLogOpen;
    [ObservableProperty] private string _newTabName = "";
    [ObservableProperty] private SheetTab? _selectedTab;
    /// <summary>What the inspector shows: the selected table row or node card.</summary>
    [ObservableProperty] private ResourceRow? _selected;
    /// <summary>Node whose guests the table is narrowed to, or null for all.</summary>
    [ObservableProperty] private string? _nodeFilter;

    public bool InspectorOpen
    {
        get => _settings.InspectorOpen;
        set
        {
            _settings.InspectorOpen = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    [ObservableProperty] private string _nodesSummary = "—";
    [ObservableProperty] private string _guestsSummary = "—";
    [ObservableProperty] private string _cpuSummary = "—";
    [ObservableProperty] private string _tempSummary = "—";
    [ObservableProperty] private int _problemsCount;

    partial void OnSearchChanged(string value) => View.Refresh();
    partial void OnOnlyProblemsChanged(bool value) => View.Refresh();
    partial void OnSelectedTabChanged(SheetTab? value) => View.Refresh();

    partial void OnSelectedChanged(ResourceRow? value) => _ = LoadAddressAsync(value);

    /// <summary>Found addresses are kept; a miss (no guest agent yet) is retried on the next selection.</summary>
    async Task LoadAddressAsync(ResourceRow? row)
    {
        if (row is null || row.Address is not null || SourceOf(row) is not { } source) return;
        row.AddressText = "…";
        row.Address = await source.AddressAsync(row.LocalId, row.Node, default);
        row.AddressText = row.Address ?? (row.LocalId.StartsWith("qemu/") ? L.T("нужен qemu-guest-agent") : L.T("неизвестен"));
    }

    partial void OnNodeFilterChanged(string? value)
    {
        foreach (var n in Nodes) n.IsFilterActive = n.Node == value;
        View.Refresh();
    }

    /// <summary>Clicking a node card selects it; clicking the filtered one again shows all guests.</summary>
    public void ToggleNodeFilter(ResourceRow node)
    {
        Selected = node;
        NodeFilter = NodeFilter == node.Node ? null : node.Node;
    }

    SourceState NewSource(IResourceSource source) => new(source, ThresholdsFor);

    // ---- servers ----

    public void EnableDemo()
    {
        if (Sources.Any(s => s.Source is DemoSource)) return;
        _settings.Demo = true;
        _settings.Save();
        Sources.Insert(0, NewSource(new DemoSource()));
    }

    public void AddServer(ServerConfig cfg, string secret)
    {
        cfg.ProtectedSecret = Secrets.Protect(secret);
        _settings.Servers.Add(cfg);
        _settings.Save();
        Sources.Add(NewSource(new ProxmoxSource(cfg, secret)));
    }

    public void RemoveSource(SourceState s)
    {
        Sources.Remove(s);
        if (s.Source is DemoSource) _settings.Demo = false;
        else _settings.Servers.RemoveAll(c => c.Key == s.Source.Key);
        _settings.Save();
        (s.Source as IDisposable)?.Dispose();
        foreach (var row in Rows.Where(r => r.SourceKey == s.Source.Key).ToList()) RemoveRow(row);
        UpdateSummary();
    }

    public IResourceSource? SourceOf(ResourceRow row) =>
        Sources.FirstOrDefault(s => s.Source.Key == row.SourceKey)?.Source;

    public ServerConfig? ConfigOf(ResourceRow row) => _settings.Servers.FirstOrDefault(c => c.Key == row.SourceKey);

    public string ExportServers() => ServerList.Export(_settings.Servers);

    public bool HasServer(ServerEntry e) => _settings.Servers.Any(s => s.Url == e.Url && s.TokenId == e.TokenId);

    public void SaveSettings() => _settings.Save();

    public void SaveTheme(string theme)
    {
        _settings.Theme = theme;
        _settings.Save();
    }

    // ---- thresholds ----

    public Thresholds GlobalThresholds => _settings.Thresholds;

    public Thresholds ThresholdsFor(string key) => _settings.ThresholdsOf.GetValueOrDefault(key) ?? _settings.Thresholds;

    public bool HasOwnThresholds(string key) => _settings.ThresholdsOf.ContainsKey(key);

    public void SetGlobalThresholds(Thresholds t)
    {
        _settings.Thresholds = t;
        _settings.Save();
    }

    /// <summary>Own danger levels for one resource; null goes back to the global ones.</summary>
    public void SetThresholds(string key, Thresholds? t)
    {
        if (t is null) _settings.ThresholdsOf.Remove(key);
        else _settings.ThresholdsOf[key] = t;
        _settings.Save();
    }

    // ---- tabs ----

    [RelayCommand]
    void CreateTab()
    {
        var name = NewTabName.Trim();
        if (name.Length == 0 || Tabs.Any(t => t.Name == name)) return;
        Tabs.Add(new SheetTab(name));
        _settings.Tabs.Add(name);
        _settings.Save();
        NewTabName = "";
        SelectedTab = Tabs[^1];
    }

    public void DeleteTab(SheetTab tab)
    {
        if (tab.IsAll) return;
        if (SelectedTab == tab) SelectedTab = Tabs[0];
        Tabs.Remove(tab);
        _settings.Tabs.Remove(tab.Name);
        RemoveAssignments(_settings.TabOf, tab.Name);
        _settings.Save();
        UpdateSummary();
    }

    /// <summary>Moves rows into a tab; null takes them out of any tab.</summary>
    public void MoveToTab(IEnumerable<ResourceRow> rows, SheetTab? tab)
    {
        foreach (var r in rows)
            if (tab is null || tab.IsAll) _settings.TabOf.Remove(r.Key);
            else _settings.TabOf[r.Key] = tab.Name;
        _settings.Save();
        View.Refresh();
        UpdateSummary();
    }

    // ---- sections: groups inside the table ----

    /// <summary>Moves rows into a section, creating it if new; null takes them out of any section.</summary>
    public void MoveToSection(IEnumerable<ResourceRow> rows, string? section)
    {
        section = section?.Trim();
        if (section is { Length: > 0 } && !_settings.Sections.Contains(section)) _settings.Sections.Add(section);
        foreach (var r in rows)
        {
            if (string.IsNullOrEmpty(section)) _settings.SectionOf.Remove(r.Key);
            else _settings.SectionOf[r.Key] = section;
            r.Section = SectionOf(r.Key);
        }
        // A section nobody is in any more disappears.
        _settings.Sections.RemoveAll(s => !_settings.SectionOf.ContainsValue(s));
        _settings.Save();
        UpdateGrouping();
    }

    string SectionOf(string key) => _settings.SectionOf.GetValueOrDefault(key) ?? NoSection;

    void UpdateGrouping()
    {
        // No sections: no group headers at all, the table stays flat.
        var want = _settings.Sections.Count > 0;
        if (want == View.GroupDescriptions.Count > 0)
        {
            View.Refresh();
            return;
        }
        if (want) View.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(ResourceRow.Section)));
        else View.GroupDescriptions.Clear();
    }

    static void RemoveAssignments(Dictionary<string, string> map, string name)
    {
        foreach (var key in map.Where(p => p.Value == name).Select(p => p.Key).ToList()) map.Remove(key);
    }

    // ---- widgets ----

    /// <summary>New widgets go side by side in rows instead of piling up on one spot.</summary>
    (double X, double Y) NextWidgetSpot()
    {
        var n = Widgets.Count;
        return (24 + 266 * (n % 5), 24 + 210 * (n / 5 % 3));
    }

    public void AddWidget(ResourceRow row)
    {
        var (x, y) = NextWidgetSpot();
        var cfg = new WidgetConfig { Key = row.Key, X = x, Y = y };
        _settings.Widgets.Add(cfg);
        _settings.Save();
        Widgets.Add(new WidgetVm(cfg) { Row = row });
    }

    /// <summary>A widget made in the widget builder.</summary>
    public void AddWidget(WidgetConfig cfg)
    {
        (cfg.X, cfg.Y) = NextWidgetSpot();
        _settings.Widgets.Add(cfg);
        _settings.Save();
        var w = new WidgetVm(cfg);
        w.Refresh(_rows);
        Widgets.Add(w);
    }

    /// <summary>Everything a widget can watch: nodes first, then VMs and containers.</summary>
    public IReadOnlyList<ResourceRow> AllResources => Rows.OrderBy(r => !r.IsNode).ThenBy(r => r.Name).ToList();

    public ResourceRow? RowOf(string key) => _rows.GetValueOrDefault(key);

    public void RemoveWidget(WidgetVm w)
    {
        Widgets.Remove(w);
        _settings.Widgets.Remove(w.Config);
        _settings.Save();
    }

    // ---- actions ----

    public async Task RunAsync(PowerAction action, IReadOnlyList<ResourceRow> rows)
    {
        foreach (var r in rows)
        {
            if (SourceOf(r) is not { } source) continue;
            try
            {
                Push(new AlertEvent(DateTime.Now, Severity.Info, r.Name, await source.ApplyAsync(r.LocalId, action, default), r.Key));
            }
            catch (Exception e)
            {
                Push(new AlertEvent(DateTime.Now, Severity.Warning, r.Name, L.F("Ошибка: {0}", ProxmoxSource.Describe(e)), r.Key));
            }
        }
    }

    [RelayCommand]
    void ClearEvents() => Events.Clear();

    /// <summary>The row for an event, with the tab and filters cleared if they hide it; null if it is gone.</summary>
    public ResourceRow? Reveal(string key)
    {
        if (!_rows.TryGetValue(key, out var row)) return null;
        if (!row.IsNode && !Matches(row))
        {
            Search = "";
            OnlyProblems = false;
            NodeFilter = null;
            SelectedTab = Tabs[0];
        }
        Selected = row;
        return row;
    }

    // ---- polling ----

    void PollDue()
    {
        var now = DateTime.UtcNow;
        foreach (var s in Sources)
            if (!s.Busy && now - s.LastPoll >= s.Source.Interval)
                _ = PollAsync(s);
    }

    async Task PollAsync(SourceState s)
    {
        s.Busy = true;
        s.LastPoll = DateTime.UtcNow;
        try
        {
            var snaps = await s.Source.FetchAsync(default);
            if (!Sources.Contains(s)) return; // removed while we were waiting
            if (!s.Online)
            {
                s.Online = true;
                Push(new AlertEvent(DateTime.Now, Severity.Info, s.Title, L.T("Связь восстановлена")));
            }
            Merge(s, snaps);
            foreach (var e in s.Engine.Observe(snaps, DateTime.Now))
                Push(e with { Id = e.Id is null ? null : s.Source.Key + ":" + e.Id });
        }
        catch (Exception e)
        {
            if (Sources.Contains(s) && s.Online)
            {
                s.Online = false;
                Push(new AlertEvent(DateTime.Now, Severity.Critical, s.Title, L.F("Нет связи: {0}", ProxmoxSource.Describe(e))));
                foreach (var row in Rows.Where(r => r.SourceKey == s.Source.Key)) row.MarkStale();
            }
        }
        finally
        {
            s.Busy = false;
        }
        UpdateSummary();
    }

    void Merge(SourceState s, IReadOnlyList<ResourceSnapshot> snaps)
    {
        var seen = new HashSet<string>();
        foreach (var snap in snaps)
        {
            var key = s.Source.Key + ":" + snap.Id;
            seen.Add(key);
            if (_rows.TryGetValue(key, out var row))
            {
                row.Update(snap, ThresholdsFor(key));
                continue;
            }
            // Fill the row before adding it: the table filter decides on IsNode when the row arrives.
            row = new ResourceRow(key, s.Source.Key, snap.Id) { Section = SectionOf(key) };
            row.Update(snap, ThresholdsFor(key));
            _rows[key] = row;
            Rows.Add(row);
            if (row.IsNode)
            {
                row.IsFilterActive = row.Node == NodeFilter;
                Nodes.Add(row);
            }
        }
        foreach (var gone in Rows.Where(r => r.SourceKey == s.Source.Key && !seen.Contains(r.Key)).ToList())
            RemoveRow(gone);
        foreach (var w in Widgets) w.Refresh(_rows);
        if (OnlyProblems) View.Refresh(); // the problem set changes every poll
    }

    void RemoveRow(ResourceRow row)
    {
        Rows.Remove(row);
        Nodes.Remove(row);
        _rows.Remove(row.Key);
        if (Selected == row) Selected = null;
        if (row.IsNode && NodeFilter == row.Node) NodeFilter = null;
        foreach (var w in Widgets) w.Refresh(_rows);
    }

    void UpdateSummary()
    {
        var nodes = Rows.Where(r => r.IsNode).ToList();
        var guests = Rows.Where(r => !r.IsNode).ToList();
        NodesSummary = $"{nodes.Count(n => n.Running && !n.Stale)}/{nodes.Count}";
        GuestsSummary = $"{guests.Count(g => g.Running && !g.Stale)}/{guests.Count}";
        CpuSummary = nodes.Count == 0 ? "—" : $"{nodes.Average(n => n.Cpu):0}%";
        var temps = nodes.Where(n => n.Temp is not null).Select(n => n.Temp!.Value).ToList();
        TempSummary = temps.Count == 0 ? "—" : $"{temps.Max():0} °C";
        ProblemsCount = Rows.Count(r => r.HasProblem);
        foreach (var tab in Tabs)
            tab.Count = tab.IsAll ? guests.Count : guests.Count(r => _settings.TabOf.GetValueOrDefault(r.Key) == tab.Name);
        foreach (var n in nodes)
        {
            var mine = guests.Where(g => g.SourceKey == n.SourceKey && g.Node == n.Node).ToList();
            n.GuestsText = L.F("{0} из {1} запущено", mine.Count(g => g.Running), mine.Count);
        }
    }

    void Push(AlertEvent e)
    {
        Events.Insert(0, e);
        if (Events.Count > MaxEvents) Events.RemoveAt(Events.Count - 1);
        Alert?.Invoke(e);
    }

    bool Matches(ResourceRow r) =>
        !r.IsNode &&
        (NodeFilter is null || r.Node == NodeFilter) &&
        (SelectedTab is null || SelectedTab.IsAll || _settings.TabOf.GetValueOrDefault(r.Key) == SelectedTab.Name) &&
        (!OnlyProblems || r.HasProblem) &&
        (Search.Length == 0
         || r.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
         || r.Node.Contains(Search, StringComparison.OrdinalIgnoreCase));
}
