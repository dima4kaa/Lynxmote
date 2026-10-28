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
public partial class SourceState(IResourceSource source, Thresholds thresholds) : ObservableObject
{
    public IResourceSource Source { get; } = source;
    public AlertEngine Engine { get; } = new(thresholds);
    public string Title => Source.Title;
    public DateTime LastPoll { get; set; }
    public bool Busy { get; set; }
    [ObservableProperty] private bool _online = true;
}

public partial class MainViewModel : ObservableObject
{
    const int MaxEvents = 300;

    readonly AppSettings _settings = AppSettings.Load();
    readonly Thresholds _thresholds = new();
    readonly Dictionary<string, ResourceRow> _rows = [];

    public MainViewModel()
    {
        View = new DataGridCollectionView(Rows) { Filter = o => Matches((ResourceRow)o) };
        Tabs.Add(new SheetTab("Все", isAll: true));
        foreach (var name in _settings.Tabs) Tabs.Add(new SheetTab(name));
        _selectedTab = Tabs[0];

        if (_settings.Demo) Sources.Add(new SourceState(new DemoSource(), _thresholds));
        foreach (var cfg in _settings.Servers)
            Sources.Add(new SourceState(new ProxmoxSource(cfg, Secrets.Unprotect(cfg.ProtectedSecret) ?? ""), _thresholds));
        Sources.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSources));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => PollDue();
        timer.Start();
    }

    public ObservableCollection<ResourceRow> Rows { get; } = [];
    public DataGridCollectionView View { get; }
    public ObservableCollection<AlertEvent> Events { get; } = [];
    public ObservableCollection<SheetTab> Tabs { get; } = [];
    public ObservableCollection<SourceState> Sources { get; } = [];
    public bool HasSources => Sources.Count > 0;
    public string Theme => _settings.Theme;

    /// <summary>Raised for every new event; the window turns warnings into toasts.</summary>
    public event Action<AlertEvent>? Alert;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _onlyProblems;
    [ObservableProperty] private bool _isLogOpen;
    [ObservableProperty] private string _newTabName = "";
    [ObservableProperty] private SheetTab? _selectedTab;

    [ObservableProperty] private string _nodesSummary = "—";
    [ObservableProperty] private string _guestsSummary = "—";
    [ObservableProperty] private string _cpuSummary = "—";
    [ObservableProperty] private string _tempSummary = "—";
    [ObservableProperty] private int _problemsCount;

    partial void OnSearchChanged(string value) => View.Refresh();
    partial void OnOnlyProblemsChanged(bool value) => View.Refresh();
    partial void OnSelectedTabChanged(SheetTab? value) => View.Refresh();

    // ---- servers ----

    public void EnableDemo()
    {
        if (Sources.Any(s => s.Source is DemoSource)) return;
        _settings.Demo = true;
        _settings.Save();
        Sources.Insert(0, new SourceState(new DemoSource(), _thresholds));
    }

    public void AddServer(ServerConfig cfg, string secret)
    {
        cfg.ProtectedSecret = Secrets.Protect(secret);
        _settings.Servers.Add(cfg);
        _settings.Save();
        Sources.Add(new SourceState(new ProxmoxSource(cfg, secret), _thresholds));
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

    public void SaveTheme(string theme)
    {
        _settings.Theme = theme;
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
        foreach (var key in _settings.TabOf.Where(p => p.Value == tab.Name).Select(p => p.Key).ToList())
            _settings.TabOf.Remove(key);
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

    // ---- actions ----

    public async Task RunAsync(PowerAction action, IReadOnlyList<ResourceRow> rows)
    {
        foreach (var r in rows)
        {
            if (Sources.FirstOrDefault(s => s.Source.Key == r.SourceKey) is not { } s) continue;
            try
            {
                Push(new AlertEvent(DateTime.Now, Severity.Info, r.Name, await s.Source.ApplyAsync(r.LocalId, action, default), r.Key));
            }
            catch (Exception e)
            {
                Push(new AlertEvent(DateTime.Now, Severity.Warning, r.Name, "Ошибка: " + ProxmoxSource.Describe(e), r.Key));
            }
        }
    }

    [RelayCommand]
    void ClearEvents() => Events.Clear();

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
                Push(new AlertEvent(DateTime.Now, Severity.Info, s.Title, "Связь восстановлена"));
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
                Push(new AlertEvent(DateTime.Now, Severity.Critical, s.Title, "Нет связи: " + ProxmoxSource.Describe(e)));
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
            if (!_rows.TryGetValue(key, out var row))
            {
                row = new ResourceRow(key, s.Source.Key, snap.Id);
                _rows[key] = row;
                Rows.Add(row);
            }
            row.Update(snap, _thresholds);
        }
        foreach (var gone in Rows.Where(r => r.SourceKey == s.Source.Key && !seen.Contains(r.Key)).ToList())
            RemoveRow(gone);
        if (OnlyProblems) View.Refresh(); // the problem set changes every poll
    }

    void RemoveRow(ResourceRow row)
    {
        Rows.Remove(row);
        _rows.Remove(row.Key);
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
            tab.Count = tab.IsAll ? Rows.Count : Rows.Count(r => _settings.TabOf.GetValueOrDefault(r.Key) == tab.Name);
    }

    void Push(AlertEvent e)
    {
        Events.Insert(0, e);
        if (Events.Count > MaxEvents) Events.RemoveAt(Events.Count - 1);
        Alert?.Invoke(e);
    }

    bool Matches(ResourceRow r) =>
        (SelectedTab is null || SelectedTab.IsAll || _settings.TabOf.GetValueOrDefault(r.Key) == SelectedTab.Name) &&
        (!OnlyProblems || r.HasProblem) &&
        (Search.Length == 0
         || r.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
         || r.Node.Contains(Search, StringComparison.OrdinalIgnoreCase));
}
