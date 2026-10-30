using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;

namespace ProxMonitor.Core;

public sealed record ConnectResult(bool Ok, string Message, string? UntrustedCert = null);

public sealed class ProxmoxException(string message) : Exception(message);

/// <summary>Proxmox VE REST API with an API token. Temperature and connections need SSH and are not read yet.</summary>
public sealed class ProxmoxSource : IResourceSource, IDisposable
{
    readonly HttpClient _http;
    readonly Dictionary<string, (long In, long Out, DateTime At, long RateIn, long RateOut)> _counters = [];
    readonly Dictionary<string, string> _nodeOf = [];
    string? _seenCert;

    public ProxmoxSource(ServerConfig cfg, string secret)
    {
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
    }

    public string Key { get; }
    public string Title { get; }
    public TimeSpan Interval => TimeSpan.FromSeconds(2); // Proxmox itself refreshes these stats about every 10 s

    public static async Task<ConnectResult> TestAsync(ServerConfig cfg, string secret)
    {
        using var source = new ProxmoxSource(cfg, secret);
        try
        {
            using var doc = await source.GetAsync("cluster/resources?type=node", default);
            return new(true, $"Подключено, нод: {doc.RootElement.GetProperty("data").GetArrayLength()}");
        }
        catch (HttpRequestException e) when (e.InnerException is AuthenticationException && source._seenCert is { } cert)
        {
            return new(false, "Сертификат сервера не подтверждён", cert);
        }
        catch (Exception e)
        {
            return new(false, Describe(e));
        }
    }

    public static string Describe(Exception e) => e switch
    {
        ProxmoxException => e.Message,
        TaskCanceledException => "Сервер не отвечает",
        HttpRequestException { InnerException: AuthenticationException } => "Сертификат сервера изменился или не подтверждён",
        _ => e.Message,
    };

    public async Task<IReadOnlyList<ResourceSnapshot>> FetchAsync(CancellationToken ct)
    {
        using var doc = await GetAsync("cluster/resources", ct);
        var now = DateTime.UtcNow;
        var list = new List<ResourceSnapshot>();
        foreach (var r in doc.RootElement.GetProperty("data").EnumerateArray())
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
            var (netIn, netOut) = Rates(id, (long)Num(r, "netin"), (long)Num(r, "netout"), now);
            list.Add(new ResourceSnapshot(
                id, k == ResourceKind.Node ? node : $"{Num(r, "vmid")} · {Str(r, "name")}", k, node,
                Str(r, "status") is "running" or "online", Num(r, "cpu"),
                (long)Num(r, "mem"), (long)Num(r, "maxmem"), (long)Num(r, "disk"), (long)Num(r, "maxdisk"),
                TempC: null, netIn, netOut, Connections: null, TimeSpan.FromSeconds(Num(r, "uptime"))));
        }
        return list;
    }

    public async Task<string> ApplyAsync(string id, PowerAction action, CancellationToken ct)
    {
        if (!id.StartsWith("qemu/") && !id.StartsWith("lxc/")) return "Действие доступно только для VM и контейнеров";
        if (!_nodeOf.TryGetValue(id, out var node)) return "Ресурс ещё не загружен";

        var path = $"nodes/{Uri.EscapeDataString(node)}/{id}";
        var form = new Dictionary<string, string>();
        string url, done;
        switch (action)
        {
            case PowerAction.Start: (url, done) = ($"{path}/status/start", "Команда запуска отправлена"); break;
            case PowerAction.Stop: (url, done) = ($"{path}/status/stop", "Команда остановки отправлена"); break;
            case PowerAction.Reboot: (url, done) = ($"{path}/status/reboot", "Команда перезагрузки отправлена"); break;
            default:
                var name = $"pm{DateTime.Now:yyyyMMdd_HHmmss}";
                form["snapname"] = name;
                (url, done) = ($"{path}/snapshot", $"Создаётся снапшот {name}");
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
            return null;
        }
    }

    static string? JoinIps(IEnumerable<string> ips)
    {
        var text = string.Join(", ", ips.Where(ip => ip.Length > 0 && !ip.StartsWith("127.")).Distinct());
        return text.Length > 0 ? text : null;
    }

    public void Dispose() => _http.Dispose();

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
            HttpStatusCode.Unauthorized => "Неверный токен",
            HttpStatusCode.Forbidden => $"Недостаточно прав: {resp.ReasonPhrase}",
            _ => $"Ошибка {(int)resp.StatusCode}: {resp.ReasonPhrase}",
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
