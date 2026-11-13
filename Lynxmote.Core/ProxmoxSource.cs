using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lynxmote.Core;

public sealed record ConnectResult(bool Ok, string Message, string? UntrustedCert = null);

public sealed class ProxmoxException(string message) : Exception(message);

/// <summary>Proxmox VE REST API with an API token; node temperature, connections and traffic come over SSH.</summary>
public sealed class ProxmoxSource : IResourceSource, IDisposable
{
    readonly HttpClient _http;
    readonly ServerConfig _cfg;
    readonly Dictionary<string, (long In, long Out, DateTime At, long RateIn, long RateOut)> _counters = [];
    readonly Dictionary<string, string> _nodeOf = [];
    readonly Dictionary<string, SshProbe<NodeReading>> _sensors = [];
    readonly Dictionary<string, SshProbe<GuestReading>> _guests = [];
    readonly Dictionary<string, GuestDisk> _disks = [];
    string? _seenCert, _shape;

    public ProxmoxSource(ServerConfig cfg, string secret)
    {
        _cfg = cfg; // the same object the terminal saves the SSH login into
        Key = cfg.Key;
        Title = cfg.Name;
        var handler = new HttpClientHandler
        {
            // Proxmox ships a self-signed cert: accept it only if its SHA-256 matches the one the user confirmed.
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            {
                _seenCert = cert?.GetCertHashString(HashAlgorithmName.SHA256);
                return errors == SslPolicyErrors.None || (cfg.PinnedCert is not null && _seenCert == cfg.PinnedCert);
            },
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(cfg.Url.TrimEnd('/') + "/api2/json/"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"PVEAPIToken={cfg.TokenId}={secret}");
        Log.Write(Title, $"server {cfg.Url}, token {cfg.TokenId}, cert pinned: {cfg.PinnedCert is not null}, ssh login: {cfg.SshUser ?? "none"}");
    }

    public string Key { get; }
    public string Title { get; }
    public TimeSpan Interval => TimeSpan.FromSeconds(2); // Proxmox itself refreshes these stats about every 10 s

    /// <summary>What is wrong with the server's answers right now, for the event log; null when all is fine.</summary>
    public string? Problem { get; private set; }

    /// <summary>
    /// A token made with "Privilege Separation" (on by default) has no rights of its own: Proxmox still lists the nodes,
    /// but without any load figures (no Sys.Audit) and hides every VM (no VM.Audit).
    /// </summary>
    static bool LacksRights(IEnumerable<JsonElement> nodes) =>
        nodes.Any() && nodes.All(n => !n.TryGetProperty("maxcpu", out _));

    static string NoRights(string tokenId) => L.F(
        "Токену {0} не хватает прав, поэтому не видно нагрузку и VM. Выдайте роль PVEAuditor на путь /: Datacenter → Permissions → Add → API Token Permission.",
        tokenId);

    public static async Task<ConnectResult> TestAsync(ServerConfig cfg, string secret)
    {
        using var source = new ProxmoxSource(cfg, secret);
        try
        {
            using var doc = await source.GetAsync("cluster/resources?type=node", default);
            Log.Write(cfg.Name, "connect test: " + doc.RootElement.GetRawText());
            var nodes = doc.RootElement.GetProperty("data").EnumerateArray().ToList();
            return LacksRights(nodes)
                ? new(false, NoRights(cfg.TokenId))
                : new(true, L.F("Подключено, нод: {0}", nodes.Count));
        }
        catch (HttpRequestException e) when (e.InnerException is AuthenticationException && source._seenCert is { } cert)
        {
            Log.Write(cfg.Name, "connect test: certificate not confirmed yet, SHA-256 " + cert);
            return new(false, L.T("Сертификат сервера не подтверждён"), cert);
        }
        catch (Exception e)
        {
            Log.Write(cfg.Name, "connect test failed: " + e);
            return new(false, Describe(e));
        }
    }

    public static string Describe(Exception e) => e switch
    {
        ProxmoxException => e.Message,
        TaskCanceledException => L.T("Сервер не отвечает"),
        HttpRequestException { InnerException: AuthenticationException } => L.T("Сертификат сервера изменился или не подтверждён"),
        _ => e.Message,
    };

    public async Task<IReadOnlyList<ResourceSnapshot>> FetchAsync(CancellationToken ct)
    {
        using var doc = await GetAsync("cluster/resources", ct);
        var now = DateTime.UtcNow;
        var list = new List<ResourceSnapshot>();
        var all = doc.RootElement.GetProperty("data").EnumerateArray().ToList();
        Problem = LacksRights(all.Where(r => Str(r, "type") == "node")) ? NoRights(_cfg.TokenId) : null;
        // The full answer only when its make-up changes, not every 2 s; Proxmox shuffles the order on every call.
        var shape = string.Join(" ", all.Select(r => Str(r, "id")).Order()) + Problem;
        if (shape != _shape) Log.Write(Title, "cluster/resources changed: " + doc.RootElement.GetRawText());
        _shape = shape;
        foreach (var r in all)
        {
            ResourceKind? kind = Str(r, "type") switch
            {
                "node" => ResourceKind.Node,
                "qemu" => ResourceKind.Vm,
                "lxc" => ResourceKind.Container,
                _ => null,
            };
            if (kind is not { } k || Num(r, "template") == 1) continue;

            var id = Str(r, "id");
            var node = Str(r, "node");
            _nodeOf[id] = node;
            var running = Str(r, "status") is "running" or "online";
            // Nodes have no netin/netout in /cluster/resources: their traffic is the NIC counters read over SSH.
            var sensors = k == ResourceKind.Node && running ? SensorsOf(node) : null;
            var reading = sensors?.Reading;
            var (netIn, netOut) = reading is { RxBytes: { } rx, TxBytes: { } tx } ? Rates(id, rx, tx, sensors!.ReadAt)
                : sensors is null ? Rates(id, (long)Num(r, "netin"), (long)Num(r, "netout"), now)
                : (0, 0);
            // Proxmox always reports disk 0 for a VM: only qemu-guest-agent knows how full its filesystems are.
            var disk = k == ResourceKind.Vm ? DiskOf(id, node, running) : null;
            // Without the agent a Linux VM is asked over SSH directly, once the user has saved a login for it.
            var guest = disk is { NoAgent: true, Windows: false } && running ? GuestOf(id) : null;
            if (guest?.Reading is { DiskTotal: > 0 } g) (disk!.Used, disk.Total) = (g.DiskUsed, g.DiskTotal);
            var (mem, maxMem) = guest?.Reading is { MemTotal: > 0 } m ? (m.MemUsed, m.MemTotal) : ((long)Num(r, "mem"), (long)Num(r, "maxmem"));
            list.Add(new ResourceSnapshot(
                id, k == ResourceKind.Node ? node : $"{Num(r, "vmid")} · {Str(r, "name")}", k, node,
                running, Num(r, "cpu"),
                mem, maxMem,
                disk?.Used ?? (long)Num(r, "disk"), disk?.Total ?? (long)Num(r, "maxdisk"),
                reading?.TempC, netIn, netOut, reading?.Connections, TimeSpan.FromSeconds(Num(r, "uptime")),
                sensors is not null ? sensors.Note ?? (reading is { TempC: null } ? L.T("нет датчика") : null)
                : guest is not null ? guest.Note : disk?.Note,
                sensors?.NeedsLogin ?? guest?.NeedsLogin ?? false));
        }
        return list;
    }

    sealed class GuestDisk
    {
        public long Used, Total; // 0 until the agent answered once; kept while the VM is off
        public string? Note;
        public DateTime Next;
        public bool Busy;
        public bool NoAgent, Windows; // what to do instead of asking the agent
        public string[] Macs = [];
    }

    GuestDisk DiskOf(string id, string node, bool running)
    {
        if (!_disks.TryGetValue(id, out var d)) _disks[id] = d = new();
        if (running && !d.Busy && DateTime.UtcNow >= d.Next)
        {
            d.Busy = true;
            _ = ReadDiskAsync(d, id, node);
        }
        return d;
    }

    async Task ReadDiskAsync(GuestDisk d, string id, string node)
    {
        try
        {
            using var doc = await GetAsync($"nodes/{Uri.EscapeDataString(node)}/{id}/agent/get-fsinfo", default);
            (d.Used, d.Total) = GuestFsUsage(doc.RootElement.GetProperty("data").GetProperty("result"));
            d.Note = null;
            d.NoAgent = false;
            d.Next = DateTime.UtcNow.AddSeconds(30);
        }
        catch (Exception e) when (e is ProxmoxException or HttpRequestException or TaskCanceledException
                                      or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            if (d.Note is null) Log.Write(Title, $"disk of {id} from the guest agent: {e.Message}");
            d.Note = L.T("нужен qemu-guest-agent");
            d.Next = DateTime.UtcNow.AddMinutes(1); // the user may install the agent meanwhile
            if (!d.NoAgent) await ReadConfigAsync(d, id, node);
            d.NoAgent = true;
        }
        finally
        {
            d.Busy = false;
        }
    }

    /// <summary>What Proxmox itself knows about a VM without the agent: its OS family and network cards.</summary>
    async Task ReadConfigAsync(GuestDisk d, string id, string node)
    {
        try
        {
            using var doc = await GetAsync($"nodes/{Uri.EscapeDataString(node)}/{id}/config", default);
            var config = doc.RootElement.GetProperty("data");
            d.Windows = Str(config, "ostype").StartsWith('w'); // win10, win11, w2k19…: no df there, only the agent helps
            d.Macs = MacsOf(config);
        }
        catch (Exception e) when (e is ProxmoxException or HttpRequestException or TaskCanceledException
                                      or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Write(Title, $"config of {id}: {e.Message}");
        }
    }

    /// <summary>MAC addresses from net0, net1…, e.g. "virtio=BC:24:11:AA:BB:CC,bridge=vmbr0".</summary>
    public static string[] MacsOf(JsonElement config) => config.EnumerateObject()
        .Where(p => p.Name.StartsWith("net") && p.Value.ValueKind == JsonValueKind.String)
        .Select(p => Regex.Match(p.Value.GetString()!, "[0-9A-Fa-f]{2}(:[0-9A-Fa-f]{2}){5}"))
        .Where(m => m.Success)
        .Select(m => m.Value.ToLowerInvariant())
        .ToArray();

    SshProbe<GuestReading> GuestOf(string id)
    {
        if (!_guests.TryGetValue(id, out var probe))
            _guests[id] = probe = new(_cfg, id, GuestProbe.Command, GuestProbe.Parse, _ => Task.FromResult(
                _cfg.GuestLogins.TryGetValue(id, out var g) ? new SshTarget(g.Host, g.User, Secrets.Unprotect(g.ProtectedPassword)) : null));
        probe.Poll();
        return probe;
    }

    /// <summary>
    /// A VM's address as its node sees it: the neighbour entry of one of the VM's network cards.
    /// Null when the node has not talked to the VM lately or has no SSH login yet.
    /// </summary>
    public string? GuessAddress(string id) =>
        _disks.TryGetValue(id, out var d) && _nodeOf.TryGetValue(id, out var node) && _sensors.TryGetValue(node, out var s) && s.Reading is { } r
            ? d.Macs.Select(m => r.Neighbors.GetValueOrDefault(m)).FirstOrDefault(ip => ip is not null)
            : null;

    /// <summary>The user has just saved an SSH login for this node or VM: read it now instead of waiting.</summary>
    public void Relogin(string id)
    {
        if (_guests.TryGetValue(id, out var guest)) guest.Retry();
        if (id.StartsWith("node/")) foreach (var s in _sensors.Values) s.Retry(); // one login serves every node of a server
    }

    /// <summary>Sum over the guest's filesystems; a device mounted twice (bind mounts, snaps) counts once.</summary>
    public static (long Used, long Total) GuestFsUsage(JsonElement fsinfo)
    {
        var seen = new HashSet<string>();
        long used = 0, total = 0;
        foreach (var fs in fsinfo.EnumerateArray())
        {
            if (!fs.TryGetProperty("total-bytes", out var t) || t.ValueKind != JsonValueKind.Number || !seen.Add(Str(fs, "name") is { Length: > 0 } n ? n : Str(fs, "mountpoint"))) continue;
            total += t.GetInt64();
            used += (long)Num(fs, "used-bytes");
        }
        return (used, total);
    }

    SshProbe<NodeReading> SensorsOf(string node)
    {
        if (!_sensors.TryGetValue(node, out var sensors))
            _sensors[node] = sensors = new(_cfg, node, NodeSensors.Command, NodeSensors.Parse, async ct => _cfg.SshUser is { } user
                ? new SshTarget(await NodeAddressAsync(node, ct), user, Secrets.Unprotect(_cfg.ProtectedSshPassword))
                : null);
        sensors.Poll();
        return sensors;
    }

    public async Task<string> ApplyAsync(string id, PowerAction action, CancellationToken ct)
    {
        if (!id.StartsWith("qemu/") && !id.StartsWith("lxc/")) return L.T("Действие доступно только для VM и контейнеров");
        if (!_nodeOf.TryGetValue(id, out var node)) return L.T("Ресурс ещё не загружен");

        var path = $"nodes/{Uri.EscapeDataString(node)}/{id}";
        var form = new Dictionary<string, string>();
        string url, done;
        switch (action)
        {
            case PowerAction.Start: (url, done) = ($"{path}/status/start", L.T("Команда запуска отправлена")); break;
            case PowerAction.Stop: (url, done) = ($"{path}/status/stop", L.T("Команда остановки отправлена")); break;
            case PowerAction.Reboot: (url, done) = ($"{path}/status/reboot", L.T("Команда перезагрузки отправлена")); break;
            default:
                var name = $"pm{DateTime.Now:yyyyMMdd_HHmmss}";
                form["snapname"] = name;
                (url, done) = ($"{path}/snapshot", L.F("Создаётся снапшот {0}", name));
                break;
        }
        using var resp = await _http.PostAsync(url, new FormUrlEncodedContent(form), ct);
        EnsureOk(resp);
        return done;
    }

    /// <summary>The node's own IP from /cluster/status, for SSH; falls back to the API host.</summary>
    public async Task<string> NodeAddressAsync(string node, CancellationToken ct)
    {
        try
        {
            using var doc = await GetAsync("cluster/status", ct);
            foreach (var e in doc.RootElement.GetProperty("data").EnumerateArray())
                if (Str(e, "type") == "node" && Str(e, "name") == node && Str(e, "ip") is { Length: > 0 } ip)
                    return ip;
        }
        catch (Exception e) when (e is ProxmoxException or HttpRequestException or JsonException)
        {
            Log.Write(Title, $"address of node {node} from cluster/status: {e.Message}, using the API host");
        }
        return _http.BaseAddress!.Host;
    }

    public async Task<string?> AddressAsync(string id, string node, CancellationToken ct)
    {
        try
        {
            if (id.StartsWith("node/")) return await NodeAddressAsync(node, ct);
            var path = $"nodes/{Uri.EscapeDataString(node)}/{id}";
            if (id.StartsWith("lxc/"))
            {
                using var doc = await GetAsync(path + "/interfaces", ct);
                return JoinIps(doc.RootElement.GetProperty("data").EnumerateArray()
                    .Where(i => Str(i, "name") != "lo")
                    .Select(i => Str(i, "inet").Split('/')[0]));
            }
            // Proxmox learns a VM's addresses only from qemu-guest-agent.
            using var agent = await GetAsync(path + "/agent/network-get-interfaces", ct);
            return JoinIps(agent.RootElement.GetProperty("data").GetProperty("result").EnumerateArray()
                .Where(i => Str(i, "name") != "lo" && i.TryGetProperty("ip-addresses", out _))
                .SelectMany(i => i.GetProperty("ip-addresses").EnumerateArray())
                .Where(a => Str(a, "ip-address-type") == "ipv4")
                .Select(a => Str(a, "ip-address"))) ?? GuestAddress(id);
        }
        catch (Exception e) when (e is ProxmoxException or HttpRequestException or TaskCanceledException
                                      or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Write(Title, $"address of {id}: {e.Message}");
            return id.StartsWith("qemu/") ? GuestAddress(id) : null;
        }
    }

    /// <summary>Without the agent: what the VM said over SSH, else the address the user logs in to, else the node's guess.</summary>
    string? GuestAddress(string id) =>
        (_guests.TryGetValue(id, out var probe) ? probe.Reading?.Ips : null)
        ?? (_cfg.GuestLogins.TryGetValue(id, out var login) ? login.Host : null)
        ?? GuessAddress(id);

    static string? JoinIps(IEnumerable<string> ips)
    {
        var text = string.Join(", ", ips.Where(ip => ip.Length > 0 && !ip.StartsWith("127.")).Distinct());
        return text.Length > 0 ? text : null;
    }

    public void Dispose()
    {
        _http.Dispose();
        foreach (var s in _sensors.Values) s.Dispose();
        foreach (var g in _guests.Values) g.Dispose();
    }

    async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(path, ct);
        EnsureOk(resp);
        return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    static void EnsureOk(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        throw new ProxmoxException(resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized => L.T("Неверный токен"),
            HttpStatusCode.Forbidden => L.F("Недостаточно прав: {0}", resp.ReasonPhrase),
            _ => L.F("Ошибка {0}: {1}", (int)resp.StatusCode, resp.ReasonPhrase),
        });
    }

    /// <summary>netin/netout are cumulative and Proxmox refreshes them every ~10 s, so rate is taken between changes.</summary>
    (long In, long Out) Rates(string id, long netIn, long netOut, DateTime now)
    {
        if (!_counters.TryGetValue(id, out var prev))
        {
            _counters[id] = (netIn, netOut, now, 0, 0);
            return (0, 0);
        }
        if (netIn == prev.In && netOut == prev.Out) return (prev.RateIn, prev.RateOut);

        var secs = Math.Max(1, (now - prev.At).TotalSeconds);
        // Counters reset when a guest restarts: clamp to zero instead of a negative rate.
        var rateIn = Math.Max(0, (long)((netIn - prev.In) / secs));
        var rateOut = Math.Max(0, (long)((netOut - prev.Out) / secs));
        _counters[id] = (netIn, netOut, now, rateIn, rateOut);
        return (rateIn, rateOut);
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
