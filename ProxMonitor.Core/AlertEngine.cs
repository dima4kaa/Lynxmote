namespace ProxMonitor.Core;

public enum Severity { Info, Warning, Critical }

/// <param name="Id">Resource id the event is about, if any.</param>
public sealed record AlertEvent(DateTime Time, Severity Severity, string ResourceName, string Message, string? Id = null);

public sealed record Thresholds(double CpuPct = 90, double TempC = 80, double DiskPct = 90)
{
    /// <summary>How long a danger must last in total inside <see cref="Window"/> before it alerts.</summary>
    public TimeSpan Hold { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Fires once when a condition has been true for <c>hold</c> in total within the last <c>window</c>.
/// A continuous run is just a special case of that total. Re-arms after a full window without the condition.
/// </summary>
public sealed class SustainedCheck(TimeSpan hold, TimeSpan window)
{
    sealed class State
    {
        public readonly Queue<(DateTime End, TimeSpan Length)> Hits = new();
        public TimeSpan Total;
        public DateTime? Last;
        public bool Fired;
    }

    readonly Dictionary<string, State> _states = [];

    public bool Observe(string key, bool active, DateTime now)
    {
        if (!_states.TryGetValue(key, out var s)) _states[key] = s = new State();

        // A sample stands for the time since the previous sample.
        if (active && s.Last is { } last && now > last)
        {
            s.Hits.Enqueue((now, now - last));
            s.Total += now - last;
        }
        s.Last = now;

        // shortcut: a hit straddling the window edge counts whole until it leaves; exact enough at sub-second sampling.
        while (s.Hits.Count > 0 && s.Hits.Peek().End <= now - window)
            s.Total -= s.Hits.Dequeue().Length;
        if (s.Hits.Count == 0) s.Fired = false;

        if (s.Fired || s.Total < hold) return false;
        s.Fired = true;
        return true;
    }
}

/// <summary>Turns a stream of snapshots from one source into events.</summary>
public sealed class AlertEngine(Thresholds t)
{
    readonly SustainedCheck _hold = new(t.Hold, t.Window);
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
