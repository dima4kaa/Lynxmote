using Renci.SshNet;
using Renci.SshNet.Common;

namespace Lynxmote.Core;

/// <summary>Where and as whom to log in over SSH; no password means this Windows user's SSH key.</summary>
public sealed record SshTarget(string Host, string User, string? Password);

public static class SshLogin
{
    // Loaded once: a key added while the app runs is picked up on the next start.
    static readonly Lazy<IPrivateKeySource[]> Keys = new(() =>
    {
        var keys = new List<IPrivateKeySource>();
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        foreach (var name in new[] { "id_ed25519", "id_ecdsa", "id_rsa" })
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) continue;
            try
            {
                keys.Add(new PrivateKeyFile(path));
            }
            catch (Exception e) // a key with a passphrase or in a format SSH.NET can't read
            {
                Log.Write("ssh", $"key {name} skipped: {e.Message}");
            }
        }
        return [.. keys];
    });

    public static bool HasKeys => Keys.Value.Length > 0;

    /// <summary>The user's SSH keys first, then the password; null when there is neither.</summary>
    /// <exception cref="ArgumentException">Empty host or user.</exception>
    public static ConnectionInfo? Info(string host, string user, string? password)
    {
        var methods = new List<AuthenticationMethod>();
        if (HasKeys) methods.Add(new PrivateKeyAuthenticationMethod(user, Keys.Value));
        if (!string.IsNullOrEmpty(password)) methods.Add(new PasswordAuthenticationMethod(user, password));
        return methods.Count == 0 ? null : new ConnectionInfo(host, user, [.. methods]) { Timeout = TimeSpan.FromSeconds(5) };
    }
}

/// <summary>
/// Runs one read-only command on a machine over SSH every 10 s and keeps the parsed answer.
/// Works on its own from the first poll once a login is saved and the host key is confirmed.
/// </summary>
public sealed class SshProbe<T>(ServerConfig cfg, string what, string command, Func<string, T> parse,
    Func<CancellationToken, Task<SshTarget?>> target) : IDisposable where T : class
{
    static readonly TimeSpan Every = TimeSpan.FromSeconds(10), AfterError = TimeSpan.FromMinutes(1);

    SshClient? _ssh;
    DateTime _next;
    bool _busy, _reset;
    string? _refused; // a login the machine rejected is not retried, so fail2ban doesn't ban this PC
    string? _host;

    public T? Reading { get; private set; }
    public DateTime ReadAt { get; private set; }
    /// <summary>Why there is no reading, short enough for a table cell; null when fine.</summary>
    public string? Note { get; private set; } = "…"; // until the first read
    /// <summary>Only the user can fix it: no saved login, an unconfirmed host key or a rejected login.</summary>
    public bool NeedsLogin { get; private set; }

    /// <summary>Starts a background read if one is due; results show up on a later poll.</summary>
    public void Poll()
    {
        if (_busy || DateTime.UtcNow < _next) return;
        _busy = true;
        _next = DateTime.UtcNow + Every;
        _ = ReadAsync();
    }

    /// <summary>The user has just saved a login: forget the refusal and read on the next poll.</summary>
    public void Retry()
    {
        _reset = true; // the connection is dropped by the reader itself, never under a running command
        _next = default;
    }

    async Task ReadAsync()
    {
        var login = "";
        try
        {
            if (_reset)
            {
                _reset = false;
                _refused = null;
                _ssh?.Dispose();
                _ssh = null;
            }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var t = await target(cts.Token);
            _host = t?.Host;
            if (t is null || SshLogin.Info(t.Host, t.User, t.Password) is not { } info)
            {
                Ask("no SSH login saved yet");
                return;
            }
            login = $"{t.Host}\n{t.User}\n{t.Password}";
            if (_refused == login) return;

            if (_ssh is not { IsConnected: true })
            {
                // The user confirms a host key once, in the login window or the terminal; never trust an unknown one silently.
                if (!cfg.SshHostKeys.TryGetValue(t.Host, out var key))
                {
                    Ask("host key not confirmed yet");
                    return;
                }
                _ssh?.Dispose();
                _ssh = new SshClient(info);
                _ssh.HostKeyReceived += (_, e) => e.CanTrust = e.FingerPrintSHA256 == key;
                await _ssh.ConnectAsync(cts.Token);
            }
            using var cmd = _ssh.CreateCommand(command);
            await cmd.ExecuteAsync(cts.Token);
            Reading = parse(cmd.Result);
            ReadAt = DateTime.UtcNow;
            NeedsLogin = false;
            SetNote(null, $"raw output:\n{cmd.Result}");
        }
        catch (SshAuthenticationException e)
        {
            _refused = login;
            NeedsLogin = true;
            Drop(L.T("SSH: вход отклонён"), e.Message);
        }
        catch (Exception e)
        {
            _next = DateTime.UtcNow + AfterError;
            NeedsLogin = false;
            Drop(L.T("SSH недоступен"), e.ToString());
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Logs only changes, so a machine read every 10 s doesn't flood the log.</summary>
    void SetNote(string? note, string detail)
    {
        if (note != Note) Log.Write($"{cfg.Name} ssh {what} {_host}", $"{note ?? "ok"}: {detail}");
        Note = note;
    }

    void Ask(string detail)
    {
        Reading = null;
        NeedsLogin = true;
        SetNote(L.T("нужен вход"), detail);
    }

    void Drop(string note, string detail)
    {
        Reading = null;
        SetNote(note, detail);
        _ssh?.Dispose();
        _ssh = null;
    }

    public void Dispose() => _ssh?.Dispose();
}

/// <summary>What a Linux VM without qemu-guest-agent reports over SSH.</summary>
public sealed record GuestReading(long DiskUsed, long DiskTotal, string? Ips, long MemUsed = 0, long MemTotal = 0);

public static class GuestProbe
{
    /// <summary>
    /// Real disks only (no tmpfs, no snap loops), the addresses of real network cards and the memory programs really
    /// hold: from outside, Proxmox counts the guest's file cache as used, so a mostly idle VM looks almost full.
    /// </summary>
    public static readonly string Command = """
        df -Pk 2>/dev/null | awk 'NR>1 && $1 ~ "^/dev/" && $1 !~ "^/dev/loop" {print "D|"$1"|"$2"|"$3}'
        ip -4 -o addr show scope global 2>/dev/null | awk '$2 !~ "^(docker|br-|veth|virbr)" {print "I|"$4}'
        awk '$1 == "MemTotal:" {t=$2} $1 == "MemAvailable:" {a=$2} END {if (t && a) print "M|"t"|"a}' /proc/meminfo 2>/dev/null
        """.ReplaceLineEndings("\n"); // a CRLF checkout must not send \r to the shell

    /// <summary>Sum over the filesystems; a device mounted twice (bind mounts) counts once.</summary>
    public static GuestReading Parse(string output)
    {
        var seen = new HashSet<string>();
        var ips = new List<string>();
        long used = 0, total = 0, memUsed = 0, memTotal = 0;
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            var p = line.Split('|');
            if (p is ["M", var allKb, var freeKb] && long.TryParse(allKb, out var all) && long.TryParse(freeKb, out var free))
                (memTotal, memUsed) = (all * 1024, (all - free) * 1024);
            else if (p is ["D", var device, var totalKb, var usedKb] && long.TryParse(totalKb, out var t) && long.TryParse(usedKb, out var u) && seen.Add(device))
                (total, used) = (total + t * 1024, used + u * 1024);
            else if (p is ["I", var address] && address.Split('/')[0] is { Length: > 0 } ip && !ips.Contains(ip))
                ips.Add(ip);
        }
        return new(used, total, ips.Count > 0 ? string.Join(", ", ips) : null, memUsed, memTotal);
    }
}
