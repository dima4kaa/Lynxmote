namespace Lynxmote.Core;

/// <summary>What a node reports over SSH; byte counters are cumulative.</summary>
/// <param name="Neighbors">MAC (lower case) -> IPv4 of the machines the node has talked to: how a VM's address is guessed.</param>
public sealed record NodeReading(double? TempC, int? Connections, long? RxBytes, long? TxBytes, IReadOnlyDictionary<string, string> Neighbors);

/// <summary>
/// Temperature, TCP connections and NIC traffic of one node. The Proxmox API reports none of them,
/// so an <see cref="SshProbe{T}"/> reads them over SSH.
/// </summary>
public static class NodeSensors
{
    /// <summary>Plain sysfs reads, no lm-sensors needed; values are millidegrees.</summary>
    public static readonly string Command = """
        for f in /sys/class/hwmon/hwmon*/temp*_input; do echo "T|$(cat ${f%/*}/name 2>/dev/null)|$(cat $f 2>/dev/null)"; done
        for z in /sys/class/thermal/thermal_zone*; do echo "T|$(cat $z/type 2>/dev/null)|$(cat $z/temp 2>/dev/null)"; done
        for i in /sys/class/net/*; do [ -e $i/device ] && echo "N|$(cat $i/statistics/rx_bytes)|$(cat $i/statistics/tx_bytes)"; done
        echo "C|$(ss -Htn state established 2>/dev/null | wc -l)"
        ip -4 neigh 2>/dev/null | awk '$4=="lladdr" {print "A|"$1"|"$5}'
        """.ReplaceLineEndings("\n"); // a CRLF checkout must not send \r to bash

    static readonly string[] CpuSensors = ["coretemp", "k10temp", "zenpower", "x86_pkg_temp", "cpu_thermal", "soc_thermal"];

    /// <summary>The CPU sensor if there is one, else the board (acpitz), else the hottest of whatever exists.</summary>
    public static NodeReading Parse(string output)
    {
        var temps = new List<(int Rank, double C)>();
        int? connections = null;
        long? rx = null, tx = null;
        var neighbors = new Dictionary<string, string>();
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            var p = line.Split('|');
            if (p is ["T", var name, var milli] && long.TryParse(milli, out var m) && m / 1000.0 is > 0 and < 150 and var c)
                temps.Add((CpuSensors.Contains(name) ? 0 : name == "acpitz" ? 1 : 2, c));
            else if (p is ["N", var r, var t] && long.TryParse(r, out var rb) && long.TryParse(t, out var tb))
                (rx, tx) = ((rx ?? 0) + rb, (tx ?? 0) + tb);
            else if (p is ["C", var n] && int.TryParse(n, out var count))
                connections = count;
            else if (p is ["A", var ip, var mac])
                neighbors[mac.ToLowerInvariant()] = ip;
        }
        double? temp = temps.Count == 0 ? null : temps.Where(x => x.Rank == temps.Min(y => y.Rank)).Max(x => x.C);
        return new(temp, connections, rx, tx, neighbors);
    }
}
