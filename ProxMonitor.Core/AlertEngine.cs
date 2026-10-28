namespace ProxMonitor.Core;

public enum Severity { Info, Warning, Critical }

/// <param name="Id">Resource id the event is about, if any.</param>
public sealed record AlertEvent(DateTime Time, Severity Severity, string ResourceName, string Message, string? Id = null);

/// <summary>Danger levels; set globally in settings or per server in its properties.</summary>
public sealed record Thresholds(double CpuPct = 90, double TempC = 80, double DiskPct = 90);

/// <summary>
/// Fires once when a condition shows up in <c>consecutive</c> data updates in a row, or in <c>inWindow</c> updates
/// within <c>window</c>. Counting updates instead of seconds ignores one-update blips at any data rate:
/// Proxmox refreshes its stats about every 10 s, the demo every 0.5 s. Re-arms after a window without the condition.
/// </summary>
public sealed class SustainedCheck(int consecutive, int inWindow, TimeSpan window)
{
    sealed class State
    {
        public readonly Queue<DateTime> Hits = new();
        public int Run;
        public bool Fired;
    }

    readonly Dictionary<string, State> _states = [];

    public bool Observe(string key, bool active, DateTime now)
    {
        if (!_states.TryGetValue(key, out var s)) _states[key] = s = new State();

        if (active)
        {
            s.Hits.Enqueue(now);
            s.Run++;
        }
        else
        {
            s.Run = 0;
        }
        while (s.Hits.Count > 0 && s.Hits.Peek() <= now - window) s.Hits.Dequeue();
        if (s.Hits.Count == 0) s.Fired = false;

        if (s.Fired || (s.Run < consecutive && s.Hits.Count < inWindow)) return false;
        s.Fired = true;
        return true;
    }
}

/// <summary>Turns a stream of snapshots from one source into events.</summary>
/// <param name="thresholdsFor">Danger levels for a resource id, read on every update so edits apply at once.</param>
public sealed class AlertEngine(Func<string, Thresholds> thresholdsFor)
{
    // Danger in 2 updates in a row, or in 3 within a minute.
    readonly SustainedCheck _hold = new(consecutive: 2, inWindow: 3, TimeSpan.FromMinutes(1));
    Dictionary<string, ResourceSnapshot>? _last;

    /// <summary>The first call only records state, except for sustained-danger tracking.</summary>
    public List<AlertEvent> Observe(IReadOnlyList<ResourceSnapshot> curr, DateTime now)
    {
        var events = new List<AlertEvent>();
        var old = _last;
        _last = curr.ToDictionary(r => r.Id);

        foreach (var r in curr)
        {
            ResourceSnapshot? p = null;
            if (old is not null && !old.Remove(r.Id, out p))
                events.Add(new(now, Severity.Info, r.Name, "Появился новый ресурс", r.Id));
            if (p is not null && p.Running != r.Running)
                events.Add(r.Running
                    ? new(now, Severity.Info, r.Name, "Запущен", r.Id)
                    : new(now, Severity.Critical, r.Name, "Остановлен", r.Id));

            // We poll faster than Proxmox refreshes; an identical snapshot is the same data update, not a new one.
            if (r == p) continue;
            var t = thresholdsFor(r.Id);
            if (_hold.Observe(r.Id + "|cpu", r.Cpu * 100 >= t.CpuPct, now))
                events.Add(new(now, Severity.Warning, r.Name, $"Нагрузка CPU {r.Cpu * 100:0}% (порог {t.CpuPct:0}%)", r.Id));
            if (_hold.Observe(r.Id + "|temp", r.TempC >= t.TempC, now))
                events.Add(new(now, Severity.Critical, r.Name, $"Перегрев {r.TempC:0} °C (порог {t.TempC:0} °C)", r.Id));
            var disk = r.DiskTotal == 0 ? 0 : 100.0 * r.DiskUsed / r.DiskTotal;
            if (_hold.Observe(r.Id + "|disk", disk >= t.DiskPct, now))
                events.Add(new(now, Severity.Warning, r.Name, $"Диск заполнен на {disk:0}%", r.Id));
        }

        if (old is not null)
            foreach (var gone in old.Values)
                events.Add(new(now, Severity.Critical, gone.Name, "Пропал из кластера", gone.Id));
        return events;
    }
}
