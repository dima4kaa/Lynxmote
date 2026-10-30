using System.Diagnostics;
using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ProxMonitor.Core;

public sealed record CommandResult(string Output, int ExitCode, TimeSpan Duration);

/// <summary>
/// Runs one command at a time on a server. Commands are independent; only the working folder carries over,
/// so interactive programs (top, nano) don't work: use their batch forms (top -bn1).
/// </summary>
public interface ITerminal : IDisposable
{
    string Prompt { get; }
    Task<CommandResult> RunAsync(string command, CancellationToken ct);
}

public enum TerminalTarget { Node, Container, Vm }

public static class Shell
{
    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public static bool IsCd(string command) => command == "cd" || command.StartsWith("cd ");

    /// <summary>The line sent to the node: keeps the working folder and reaches into guests via pct/qm.</summary>
    public static string Compose(string command, string cwd, TerminalTarget target, int vmid)
    {
        var inner = cwd == "~" ? command : $"cd {Quote(cwd)} && {command}";
        return target switch
        {
            TerminalTarget.Node => $"{{ {inner}\n}} 2>&1",
            TerminalTarget.Container => $"pct exec {vmid} -- bash -lc {Quote(inner + " 2>&1")}",
            _ => $"qm guest exec {vmid} -- bash -lc {Quote(inner + " 2>&1")}",
        };
    }

    /// <summary>qm guest exec answers with JSON; anything else is qm's own error (e.g. no guest agent).</summary>
    public static (string Output, int ExitCode) ParseGuestExec(string text, int exitCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            var output = (r.TryGetProperty("out-data", out var o) ? o.GetString() : "") +
                         (r.TryGetProperty("err-data", out var e) ? e.GetString() : "");
            return (output, r.TryGetProperty("exitcode", out var c) ? c.GetInt32() : exitCode);
        }
        catch (JsonException)
        {
            return (text, exitCode);
        }
    }
}

public sealed record SshConnectResult(SshTerminal? Terminal, string? UnknownHostKey = null, string? Error = null);

/// <summary>SSH to a Proxmox node; containers and VMs are reached from the node with pct exec / qm guest exec.</summary>
public sealed class SshTerminal : ITerminal
{
    readonly SshClient _ssh;
    readonly string _user, _label;
    readonly TerminalTarget _target;
    readonly int _vmid;
    string _cwd = "~";

    SshTerminal(SshClient ssh, string user, string label, TerminalTarget target, int vmid) =>
        (_ssh, _user, _label, _target, _vmid) = (ssh, user, label, target, vmid);

    public string Prompt => $"{_user}@{_label}:{_cwd}{(_user == "root" ? "#" : "$")}";

    /// <param name="pinnedHostKey">SHA-256 fingerprint the user confirmed for this host; null on first contact.</param>
    public static async Task<SshConnectResult> ConnectAsync(
        string host, string user, string password, string? pinnedHostKey,
        string label, TerminalTarget target, int vmid, CancellationToken ct)
    {
        var client = new SshClient(host, user, password);
        string? seen = null;
        client.HostKeyReceived += (_, e) =>
        {
            seen = e.FingerPrintSHA256;
            e.CanTrust = pinnedHostKey is not null && seen == pinnedHostKey;
        };
        try
        {
            await client.ConnectAsync(ct);
            return new(new SshTerminal(client, user, label, target, vmid));
        }
        catch (Exception e)
        {
            client.Dispose();
            if (seen is not null && seen != pinnedHostKey) return new(null, UnknownHostKey: seen);
            return new(null, Error: e is SshAuthenticationException ? L.T("Неверный логин или пароль") : e.Message);
        }
    }

    public async Task<CommandResult> RunAsync(string command, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var isCd = Shell.IsCd(command);
        using var cmd = _ssh.CreateCommand(Shell.Compose(isCd ? command + " && pwd" : command, _cwd, _target, _vmid));
        await cmd.ExecuteAsync(ct);
        var output = cmd.Result;
        var code = cmd.ExitStatus ?? -1;
        if (_target == TerminalTarget.Vm) (output, code) = Shell.ParseGuestExec(output, code);
        if (isCd && code == 0)
        {
            _cwd = output.Trim().Split('\n')[^1];
            output = "";
        }
        return new(output.TrimEnd(), code, sw.Elapsed);
    }

    public void Dispose() => _ssh.Dispose();
}

/// <summary>Pretend shell for the demo cluster, answers a handful of common admin commands.</summary>
public sealed class DemoTerminal(string host) : ITerminal
{
    string _cwd = "~";

    public string Prompt => $"root@{host}:{_cwd}#";

    public async Task<CommandResult> RunAsync(string command, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await Task.Delay(120 + Random.Shared.Next(200), ct); // feels like a network round trip
        var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var arg = parts.Length > 1 ? parts[1].Trim() : "";
        var (output, code) = parts[0] switch
        {
            "cd" => Cd(arg),
            "pwd" => (_cwd == "~" ? "/root" : _cwd, 0),
            "whoami" => ("root", 0),
            "hostname" => (host, 0),
            "echo" => (arg, 0),
            "ls" => ("backup  scripts  notes.txt", 0),
            "uptime" => ($" {DateTime.Now:HH:mm:ss} up 79 days,  3:12,  1 user,  load average: 0.42, 0.37, 0.35", 0),
            "df" => ("""
                Filesystem            Size  Used Avail Use% Mounted on
                /dev/mapper/pve-root   94G   38G   52G  43% /
                /dev/nvme0n1p2        1.0G   12M 1012M   2% /boot/efi
                local-lvm             1.8T  812G  1.0T  45% /var/lib/vz
                """, 0),
            "free" => ("""
                               total        used        free      shared  buff/cache   available
                Mem:           125Gi        25Gi        88Gi       1.1Gi        13Gi        99Gi
                Swap:          8.0Gi          0B       8.0Gi
                """, 0),
            "pveversion" => ("pve-manager/8.4.1 (running kernel: 6.8.12-10-pve)", 0),
            "qm" => ("""
                      VMID NAME                 STATUS     MEM(MB)    BOOTDISK(GB) PID
                       100 web-frontend         running    8192              80.00 2211
                       101 db-postgres          running    32768            500.00 2304
                       106 win-srv-2022         stopped    16384            200.00 0
                """, 0),
            "pct" => ("""
                VMID       Status     Lock         Name
                103        running                 nextcloud
                104        running                 dns-01
                110        running                 backup
                """, 0),
            "ss" => ("""
                tcp   0      0      10.0.10.11:8006    10.0.10.50:51234   users:(("pveproxy worker",pid=2231,fd=12))
                tcp   0      0      10.0.10.11:22      10.0.10.50:50122   users:(("sshd",pid=1904,fd=4))
                tcp   0      0      10.0.10.11:5432    10.0.10.21:40418   users:(("postgres",pid=3310,fd=9))
                tcp   0      0      10.0.10.11:443     10.0.20.7:61002    users:(("nginx",pid=1288,fd=17))
                udp   0      0      10.0.10.11:123     10.0.0.1:123       users:(("chronyd",pid=812,fd=6))
                """, 0),
            "systemctl" => ("""
                ● pveproxy.service - PVE API Proxy Server
                     Loaded: loaded (/lib/systemd/system/pveproxy.service; enabled)
                     Active: active (running) since Mon 2026-08-10 09:14:02 MSK; 2 months 18 days ago
                """, 0),
            _ => (L.F("bash: {0}: команда недоступна в демо-режиме\nПопробуйте: uptime, df -h, free -h, qm list, pct list, pveversion, ls, cd, pwd", parts[0]), 127),
        };
        return new(output, code, sw.Elapsed);
    }

    (string, int) Cd(string arg)
    {
        _cwd = arg is "" or "~" ? "~" : arg.StartsWith('/') ? arg : (_cwd == "~" ? "/root/" : _cwd + "/") + arg;
        return ("", 0);
    }

    public void Dispose() { }
}
