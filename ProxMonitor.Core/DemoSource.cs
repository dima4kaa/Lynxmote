namespace ProxMonitor.Core;

/// <summary>Fake 3-node cluster with random incidents, for showing the app without a real Proxmox.</summary>
public sealed class DemoSource : IResourceSource
{
    sealed class Item
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Node { get; init; }
        public ResourceKind Kind { get; init; }
        public long MemTotal { get; init; }
        public long DiskTotal { get; init; }
        public bool Running = true;
        public double Cpu, Mem, Disk, Net, Temp;
        public int Conn;
        public int Hot; // ticks left of an incident: CPU spike for guests, overheat for nodes
        public TimeSpan Uptime;
    }

    const long GB = 1L << 30;
    readonly Random _rnd;
    readonly List<Item> _items = [];
    DateTime? _lastFetch;

    public string Key => "demo";
    public string Title => "Демо-кластер";
    public TimeSpan Interval => TimeSpan.FromMilliseconds(500);

    public DemoSource(int seed = 7)
    {
        _rnd = new Random(seed);
        string[] nodes = ["pve-01", "pve-02", "pve-03"];
        foreach (var n in nodes)
            _items.Add(new Item
            {
                Id = "node/" + n, Name = n, Node = n, Kind = ResourceKind.Node,
                MemTotal = 128 * GB, DiskTotal = 2000 * GB,
                Disk = 0.3 + _rnd.NextDouble() * 0.3, Uptime = TimeSpan.FromDays(_rnd.Next(5, 90)),
            });

        (string Name, ResourceKind Kind, int MemGb, int DiskGb)[] guests =
        [
            ("web-frontend", ResourceKind.Vm, 8, 80), ("db-postgres", ResourceKind.Vm, 32, 500),
            ("gitlab", ResourceKind.Vm, 16, 300), ("nextcloud", ResourceKind.Container, 8, 1000),
            ("dns-01", ResourceKind.Container, 1, 8), ("monitoring", ResourceKind.Container, 4, 100),
            ("win-srv-2022", ResourceKind.Vm, 16, 200), ("k8s-master", ResourceKind.Vm, 8, 100),
            ("k8s-worker-1", ResourceKind.Vm, 16, 200), ("k8s-worker-2", ResourceKind.Vm, 16, 200),
            ("backup", ResourceKind.Container, 4, 4000), ("mail", ResourceKind.Container, 4, 150),
        ];
        var vmid = 100;
        foreach (var g in guests)
        {
            _items.Add(new Item
            {
                Id = (g.Kind == ResourceKind.Vm ? "qemu/" : "lxc/") + vmid,
                Name = $"{vmid} · {g.Name}", Node = nodes[vmid % nodes.Length], Kind = g.Kind,
                MemTotal = g.MemGb * GB, DiskTotal = g.DiskGb * GB,
                Cpu = _rnd.NextDouble() * 0.4, Mem = 0.3 + _rnd.NextDouble() * 0.4, Disk = 0.2 + _rnd.NextDouble() * 0.5,
                Conn = _rnd.Next(5, 120), Uptime = TimeSpan.FromHours(_rnd.Next(1, 2000)),
            });
            vmid++;
        }
        _items.Single(i => i.Name.EndsWith("backup")).Disk = 0.895; // crosses the 90% disk alert a few minutes into a demo
        _items.Single(i => i.Name.EndsWith("win-srv-2022")).Running = false;
    }

    public Task<IReadOnlyList<ResourceSnapshot>> FetchAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var elapsed = _lastFetch is { } last ? now - last : Interval;
        _lastFetch = now;
        return Task.FromResult(Next(elapsed));
    }

    public Task<string> ApplyAsync(string id, PowerAction action, CancellationToken ct) =>
        Task.FromResult(Apply(id, action));

    public IReadOnlyList<ResourceSnapshot> Next(TimeSpan elapsed)
    {
        if (_rnd.NextDouble() < 0.04) Incident();
        foreach (var g in _items.Where(i => i.Kind != ResourceKind.Node)) StepGuest(g, elapsed);
        foreach (var n in _items.Where(i => i.Kind == ResourceKind.Node)) StepNode(n, elapsed);
        return _items.Select(ToSnapshot).ToList();
    }

    string Apply(string id, PowerAction action)
    {
        var item = _items.Single(i => i.Id == id);
        if (item.Kind == ResourceKind.Node) return "В демо-режиме действия с нодами отключены";
        switch (action)
        {
            case PowerAction.Start:
                if (item.Running) return "Уже запущен";
                Start(item);
                return "Отправлена команда запуска";
            case PowerAction.Stop:
                if (!item.Running) return "Уже остановлен";
                item.Running = false;
                return "Отправлена команда остановки";
            case PowerAction.Reboot:
                if (!item.Running) return "Не запущен";
                item.Uptime = TimeSpan.Zero;
                item.Cpu = 0.6;
                return "Перезагружается";
            default:
                return $"Создан снапшот pm{DateTime.Now:yyyyMMdd_HHmmss}";
        }
    }

    void Incident()
    {
        var guests = _items.Where(i => i.Kind != ResourceKind.Node).ToList();
        switch (_rnd.Next(5))
        {
            case 0:
                if (Pick(guests.Where(g => g.Running)) is { } victim) victim.Running = false;
                break;
            case 1:
                if (Pick(guests.Where(g => !g.Running)) is { } back) Start(back);
                break;
            case 2:
                if (Pick(guests.Where(g => g.Running)) is { } busy) busy.Hot = 8; // 4 s spike: alerts
                break;
            case 3:
                if (Pick(guests.Where(g => g.Running)) is { } blip) blip.Hot = 1; // 0.5 s blip: alerts only if it repeats within a minute
                break;
            default:
                if (Pick(_items.Where(i => i.Kind == ResourceKind.Node)) is { } node) node.Hot = 6;
                break;
        }
    }

    void StepGuest(Item g, TimeSpan elapsed)
    {
        if (!g.Running)
        {
            g.Cpu = g.Mem = g.Net = 0;
            g.Conn = 0;
            g.Uptime = TimeSpan.Zero;
            return;
        }
        g.Cpu = g.Hot > 0 ? 0.92 + _rnd.NextDouble() * 0.07 : Math.Clamp(g.Cpu + Jitter(0.05), 0.02, 0.7);
        if (g.Hot > 0) g.Hot--;
        g.Mem = Math.Clamp(g.Mem + Jitter(0.015), 0.15, 0.95);
        g.Disk = Math.Min(0.99, g.Disk + _rnd.NextDouble() * 0.0003);
        g.Net = g.Cpu * 8_000_000 * (0.5 + _rnd.NextDouble());
        g.Conn = Math.Max(0, g.Conn + _rnd.Next(-2, 3));
        g.Uptime += elapsed;
    }

    void StepNode(Item n, TimeSpan elapsed)
    {
        var guests = _items.Where(i => i.Node == n.Node && i.Kind != ResourceKind.Node && i.Running).ToList();
        n.Cpu = Math.Clamp(0.04 + guests.Sum(g => g.Cpu) / 5 + Jitter(0.02), 0, 1);
        n.Mem = Math.Clamp(0.05 + guests.Sum(g => g.Mem * g.MemTotal) / n.MemTotal, 0, 1);
        n.Net = guests.Sum(g => g.Net);
        n.Conn = guests.Sum(g => g.Conn);
        n.Disk = Math.Min(0.99, n.Disk + _rnd.NextDouble() * 0.00005);
        n.Temp = 40 + n.Cpu * 35 + (n.Hot > 0 ? 38 : 0) + Jitter(1.5);
        if (n.Hot > 0) n.Hot--;
        n.Uptime += elapsed;
    }

    void Start(Item g)
    {
        g.Running = true;
        g.Cpu = 0.1;
        g.Mem = 0.3;
    }

    Item? Pick(IEnumerable<Item> items)
    {
        var list = items.ToList();
        return list.Count == 0 ? null : list[_rnd.Next(list.Count)];
    }

    double Jitter(double amplitude) => (_rnd.NextDouble() - 0.5) * 2 * amplitude;

    static ResourceSnapshot ToSnapshot(Item i) => new(
        i.Id, i.Name, i.Kind, i.Node, i.Running, i.Cpu,
        (long)(i.Mem * i.MemTotal), i.MemTotal, (long)(i.Disk * i.DiskTotal), i.DiskTotal,
        i.Kind == ResourceKind.Node ? i.Temp : null,
        (long)i.Net, (long)(i.Net * 0.4), i.Conn, i.Uptime);
}
