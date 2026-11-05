using Renci.SshNet;
using Renci.SshNet.Common;

namespace ProxMonitor.Core;

/// <summary>What a node reports over SSH; byte counters are cumulative.</summary>
public sealed record NodeReading(double? TempC, int? Connections, long? RxBytes, long? TxBytes);

/// <summary>
/// Temperature, TCP connections and NIC traffic of one node. The Proxmox API reports none of them,
/// so they are read over SSH with the login saved in the terminal, every 10 s.
/// </summary>
public sealed class NodeSensors(ServerConfig cfg, Func<CancellationToken, Task<string>> address) : IDisposable
{
    /// <summary>Plain sysfs reads, no lm-sensors needed; values are millidegrees.</summary>
    public static readonly string Command = """
        for f in /sys/class/hwmon/hwmon*/temp*_input; do echo "T|$(cat ${f%/*}/name 2>/dev/null)|$(cat $f 2>/dev/null)"; done
        for z in /sys/class/thermal/thermal_zone*; do echo "T|$(cat $z/type 2>/dev/null)|$(cat $z/temp 2>/dev/null)"; done
        for i in /sys/class/net/*; do [ -e $i/device ] && echo "N|$(cat $i/statistics/rx_bytes)|$(cat $i/statistics/tx_bytes)"; done
        echo "C|$(ss -Htn state established 2>/dev/null | wc -l)"
        """.ReplaceLineEndings("\n"); // a CRLF checkout must not send \r to bash

    static readonly string[] CpuSensors = ["coretemp", "k10temp", "zenpower", "x86_pkg_temp", "cpu_thermal", "soc_thermal"];

    /// <summary>The CPU sensor if there is one, else the board (acpitz), else the hottest of whatever exists.</summary>
    public static NodeReading Parse(string output)
    {
        var temps = new List<(int Rank, double C)>();
        int? connections = null;
        long? rx = null, tx = null;
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            var p = line.Split('|');
            if (p is ["T", var name, var milli] && long.TryParse(milli, out var m) && m / 1000.0 is > 0 and < 150 and var c)
                temps.Add((CpuSensors.Contains(name) ? 0 : name == "acpitz" ? 1 : 2, c));
            else if (p is ["N", var r, var t] && long.TryParse(r, out var rb) && long.TryParse(t, out var tb))
                (rx, tx) = ((rx ?? 0) + rb, (tx ?? 0) + tb);
            else if (p is ["C", var n] && int.TryParse(n, out var count))
                connections = count;
        }
        double? temp = temps.Count == 0 ? null : temps.Where(x => x.Rank == temps.Min(y => y.Rank)).Max(x => x.C);
        return new(temp, connections, rx, tx);
    }

    static readonly TimeSpan Every = TimeSpan.FromSeconds(10), AfterError = TimeSpan.FromMinutes(1);

    SshClient? _ssh;
    DateTime _next;
    bool _busy;
    string? _refused; // a login the node rejected is not retried, so fail2ban doesn't ban this PC
    string? _host;

    public NodeReading? Reading { get; private set; }
    public DateTime ReadAt { get; private set; }
    /// <summary>Why there is no reading, short enough for the temperature field; null when fine.</summary>
    public string? Note { get; private set; } = "…"; // until the first read

    /// <summary>Starts a background read if one is due; results show up on a later poll.</summary>
    public void Poll()
    {
        if (_busy || DateTime.UtcNow < _next) return;
        _busy = true;
        _next = DateTime.UtcNow + Every;
        _ = ReadAsync();
    }

    async Task ReadAsync()
    {
        try
        {
            var password = Secrets.Unprotect(cfg.ProtectedSshPassword);
            if (cfg.SshUser is not { } user || password is null)
            {
                Fail(L.T("нужен SSH"), "no SSH login saved yet");
                return;
            }
            if (_refused == user + "\n" + password) return;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (_ssh is not { IsConnected: true })
            {
                var host = _host = await address(cts.Token);
                // The host key is confirmed once in the terminal; never trust an unknown one silently.
                if (!cfg.SshHostKeys.TryGetValue(host, out var key))
                {
                    Fail(L.T("нужен SSH"), "host key not confirmed in the terminal yet");
                    return;
                }
                _ssh?.Dispose();
                _ssh = new SshClient(host, user, password);
                _ssh.ConnectionInfo.Timeout = TimeSpan.FromSeconds(5);
                _ssh.HostKeyReceived += (_, e) => e.CanTrust = e.FingerPrintSHA256 == key;
                await _ssh.ConnectAsync(cts.Token);
            }
            using var cmd = _ssh.CreateCommand(Command);
            await cmd.ExecuteAsync(cts.Token);
            Reading = Parse(cmd.Result);
            ReadAt = DateTime.UtcNow;
            SetNote(Reading.TempC is null ? L.T("нет датчика") : null, $"{Reading}, raw output:\n{cmd.Result}");
        }
        catch (SshAuthenticationException e)
        {
            _refused = cfg.SshUser + "\n" + Secrets.Unprotect(cfg.ProtectedSshPassword);
            Drop(L.T("SSH: неверный пароль"), e.Message);
        }
        catch (Exception e)
        {
            _next = DateTime.UtcNow + AfterError;
            Drop(L.T("SSH недоступен"), e.ToString());
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Logs only changes, so a node read every 10 s doesn't flood the log.</summary>
    void SetNote(string? note, string detail)
    {
        if (note != Note) Log.Write($"{cfg.Name} ssh {_host}", $"{note ?? "ok"}: {detail}");
        Note = note;
    }

    void Fail(string note, string detail)
    {
        Reading = null;
        SetNote(note, detail);
    }

    void Drop(string note, string detail)
    {
        Fail(note, detail);
        _ssh?.Dispose();
        _ssh = null;
    }

    public void Dispose() => _ssh?.Dispose();
}
