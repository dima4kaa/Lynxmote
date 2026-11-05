using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;

namespace ProxMonitor.Core;

public sealed record ConnectResult(bool Ok, string Message, string? UntrustedCert = null);

public sealed class ProxmoxException(string message) : Exception(message);

/// <summary>Proxmox VE REST API with an API token; node temperature, connections and traffic come over SSH.</summary>
public sealed class ProxmoxSource : IResourceSource, IDisposable
{
    readonly HttpClient _http;
    readonly ServerConfig _cfg;
    readonly Dictionary<string, (long In, long Out, DateTime At, long RateIn, long RateOut)> _counters = [];
    readonly Dictionary<string, string> _nodeOf = [];
    readonly Dictionary<string, NodeSensors> _sensors = [];
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
        // The full answer only when its make-up changes, not every 2 s.
        var shape = string.Join(" ", all.Select(r => Str(r, "id"))) + Problem;
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
            list.Add(new ResourceSnapshot(
                id, k == ResourceKind.Node ? node : $"{Num(r, "vmid")} · {Str(r, "name")}", k, node,
                running, Num(r, "cpu"),
                (long)Num(r, "mem"), (long)Num(r, "maxmem"), (long)Num(r, "disk"), (long)Num(r, "maxdisk"),
                reading?.TempC, netIn, netOut, reading?.Connections, TimeSpan.FromSeconds(Num(r, "uptime")),
                sensors?.Note));
        }
        return list;
    }

    NodeSensors SensorsOf(string node)
    {
        if (!_sensors.TryGetValue(node, out var sensors))
            _sensors[node] = sensors = new NodeSensors(_cfg, ct => NodeAddressAsync(node, ct));
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
            // VMs only report addresses through qemu-guest-agent.
            using var agent = await GetAsync(path + "/agent/network-get-interfaces", ct);
            return JoinIps(agent.RootElement.GetProperty("data").GetProperty("result").EnumerateArray()
                .Where(i => Str(i, "name") != "lo" && i.TryGetProperty("ip-addresses", out _))
                .SelectMany(i => i.GetProperty("ip-addresses").EnumerateArray())
                .Where(a => Str(a, "ip-address-type") == "ipv4")
                .Select(a => Str(a, "ip-address")));
        }
        catch (Exception e) when (e is ProxmoxException or HttpRequestException or TaskCanceledException
                                      or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Write(Title, $"address of {id}: {e.Message}");
            return null;
        }
    }

    static string? JoinIps(IEnumerable<string> ips)
    {
        var text = string.Join(", ", ips.Where(ip => ip.Length > 0 && !ip.StartsWith("127.")).Distinct());
        return text.Length > 0 ? text : null;
    }

    public void Dispose()
    {
        _http.Dispose();
        foreach (var s in _sensors.Values) s.Dispose();
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
