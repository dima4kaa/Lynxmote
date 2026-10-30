using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProxMonitor.Core;

/// <summary>One server in an exported list: where and as whom, never the secrets.</summary>
public sealed record ServerEntry(string Name, string Url, string TokenId, string? SshUser = null);

/// <summary>Moves the server list between PCs. Secrets are encrypted per Windows user, so they are re-entered on import.</summary>
public static class ServerList
{
    const string FormatName = "proxmonitor-servers";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    sealed record FileModel(string? Format, int Version, List<ServerEntry>? Servers);

    public static string Export(IEnumerable<ServerConfig> servers) => JsonSerializer.Serialize(
        new FileModel(FormatName, 1, servers.Select(s => new ServerEntry(s.Name, s.Url, s.TokenId, s.SshUser)).ToList()), Json);

    /// <summary>Valid entries from a file the user picked; malformed ones are dropped, not trusted.</summary>
    /// <exception cref="FormatException">Not a ProxMonitor server list.</exception>
    public static List<ServerEntry> Import(string json)
    {
        FileModel? file;
        try
        {
            file = JsonSerializer.Deserialize<FileModel>(json, Json);
        }
        catch (JsonException)
        {
            file = null;
        }
        if (file?.Format != FormatName || file.Servers is null) throw new FormatException(L.T("Это не список серверов ProxMonitor"));
        return file.Servers.Where(IsValid).ToList();
    }

    static bool IsValid(ServerEntry? e) =>
        e is { Name: { Length: > 0 }, TokenId: { } id, Url: { } url }
        && id.Contains('@') && id.Contains('!')
        && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}

public sealed record NetConnection(string Proto, string Local, string Peer, string Process);

/// <summary>Established TCP/UDP connections, read with ss on the server.</summary>
public static partial class NetConnections
{
    public const string Command = "ss -tunpH state established";

    // Lines look like: tcp 0 0 10.0.0.5:8006 10.0.0.9:51234 users:(("pveproxy worker",pid=1234,fd=12))
    public static List<NetConnection> Parse(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 5)
            .Select(p => new NetConnection(p[0], p[3], p[4], ProcessName().Match(string.Join(' ', p[5..])).Groups[1].Value))
            .ToList();

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex ProcessName();
}
