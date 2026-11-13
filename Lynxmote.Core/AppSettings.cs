using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lynxmote.Core;

public sealed class ServerConfig
{
    public string Key { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string TokenId { get; set; } = "";
    public string? ProtectedSecret { get; set; }
    public string? PinnedCert { get; set; } // SHA-256 of a self-signed cert the user confirmed
    public string? SshUser { get; set; }
    public string? ProtectedSshPassword { get; set; } // null with SshUser set: login by SSH key
    public Dictionary<string, string> SshHostKeys { get; set; } = []; // host -> SHA-256 fingerprint the user confirmed
    public Dictionary<string, GuestLogin> GuestLogins { get; set; } = []; // "qemu/100" -> SSH straight into a VM without the agent
}

/// <param name="ProtectedPassword">Null when the VM lets this Windows user's SSH key in.</param>
public sealed record GuestLogin(string Host, string User, string? ProtectedPassword);

public static class WidgetKind
{
    public const string Server = "server";   // one resource, chosen metrics
    public const string Group = "group";     // several resources, one line each
    public const string Summary = "summary"; // totals over a selection
    public const string Top = "top";         // the most loaded in a selection
    public const string Command = "command"; // one-click command on a server
}

/// <summary>A small floating card over the table.</summary>
public sealed class WidgetConfig
{
    public string Kind { get; set; } = WidgetKind.Server;
    public string Title { get; set; } = "";
    public List<string> Keys { get; set; } = []; // selection for group/summary/top/command; empty means all
    public string Metric { get; set; } = "cpu";  // top: cpu | ram | disk
    public int Count { get; set; } = 3;          // top: how many
    public string Command { get; set; } = "";    // command: what to run
    public string Key { get; set; } = ""; // server kind: resource key
    public double X { get; set; }
    public double Y { get; set; }
    public bool Cpu { get; set; } = true;
    public bool Ram { get; set; } = true;
    public bool Disk { get; set; }
    public bool Temp { get; set; } = true;
    public bool Net { get; set; }
}

/// <summary>Everything the app remembers between runs, in %AppData%\Lynxmote\settings.json.</summary>
public sealed class AppSettings
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lynxmote", "settings.json");

    /// <summary>Takes over the settings folder left under the app's old name, so a rename keeps servers and layout.</summary>
    public static void AdoptFolder(string oldDir, string newDir)
    {
        if (Directory.Exists(newDir) || !Directory.Exists(oldDir)) return;
        try
        {
            Directory.Move(oldDir, newDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the old folder stays as it was; the app just starts with empty settings
        }
    }

    [JsonIgnore] public string FilePath { get; private set; } = DefaultPath;

    public bool Demo { get; set; }
    public string Theme { get; set; } = "Dark";
    public bool InspectorOpen { get; set; } = true;
    public bool NodesCollapsed { get; set; }
    public string Language { get; set; } = "ru";
    public List<ServerConfig> Servers { get; set; } = [];
    public List<string> Tabs { get; set; } = [];
    public Dictionary<string, string> TabOf { get; set; } = []; // resource key -> tab name
    public List<string> Sections { get; set; } = [];
    public Dictionary<string, string> SectionOf { get; set; } = []; // resource key -> section name
    public Thresholds Thresholds { get; set; } = new();
    public Dictionary<string, Thresholds> ThresholdsOf { get; set; } = []; // resource key -> own danger levels
    public List<WidgetConfig> Widgets { get; set; } = [];

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        AppSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            settings = new();
        }
        catch (JsonException)
        {
            File.Move(path, path + ".broken", overwrite: true); // keep the user's file instead of overwriting it
            settings = new();
        }
        settings.FilePath = path;
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

public static class Secrets
{
    // shortcut: DPAPI is Windows-only, so other OSes don't keep the token between runs; use the OS keychain when porting.
    public static string? Protect(string secret) => OperatingSystem.IsWindows()
        ? Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser))
        : null;

    /// <summary>An SSH password as typed; empty means the login works by key and nothing is stored.</summary>
    public static string? ProtectPassword(string? password) => string.IsNullOrEmpty(password) ? null : Protect(password);

    public static string? Unprotect(string? data)
    {
        if (data is null || !OperatingSystem.IsWindows()) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(data), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null; // settings copied from another user or PC
        }
    }
}
