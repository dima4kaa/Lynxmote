using ProxMonitor.Core;

namespace ProxMonitor.Tests;

public class AlertEngineTests
{
    static readonly DateTime T0 = new(2026, 1, 1);

    static ResourceSnapshot Snap(bool running = true, double cpu = 0.1, double? temp = 50, long diskUsed = 10, int uptime = 0) =>
        new("qemu/100", "100 · web", ResourceKind.Vm, "pve-01", running, cpu, 1, 2, diskUsed, 100, temp, 0, 0, 0, TimeSpan.FromSeconds(uptime));

    static DateTime At(double seconds) => T0.AddSeconds(seconds);

    static SustainedCheck Check() => new(consecutive: 2, inWindow: 3, TimeSpan.FromMinutes(1));

    [Fact]
    public void Single_update_blip_is_ignored()
    {
        var check = Check();
        Assert.False(check.Observe("k", true, At(0)));
        Assert.False(check.Observe("k", false, At(10)));
    }

    [Fact]
    public void Two_updates_in_a_row_fire_once()
    {
        var check = Check();
        Assert.False(check.Observe("k", true, At(0)));
        Assert.True(check.Observe("k", true, At(10)));
        Assert.False(check.Observe("k", true, At(20)));
    }

    [Fact]
    public void Three_scattered_updates_within_a_minute_fire()
    {
        var check = Check();
        Assert.False(check.Observe("k", true, At(0)));
        Assert.False(check.Observe("k", false, At(10)));
        Assert.False(check.Observe("k", true, At(20)));
        Assert.False(check.Observe("k", false, At(30)));
        Assert.True(check.Observe("k", true, At(40)));
    }

    [Fact]
    public void Blips_older_than_the_window_are_forgotten()
    {
        var check = Check();
        check.Observe("k", true, At(0));
        check.Observe("k", false, At(10));
        check.Observe("k", true, At(20));
        check.Observe("k", false, At(30));
        Assert.False(check.Observe("k", true, At(70))); // the blip at 0 s has left the window
    }

    [Fact]
    public void Rearms_after_a_quiet_window()
    {
        var check = Check();
        check.Observe("k", true, At(0));
        Assert.True(check.Observe("k", true, At(10)));
        Assert.False(check.Observe("k", false, At(75)));
        Assert.False(check.Observe("k", true, At(80)));
        Assert.True(check.Observe("k", true, At(90)));
    }

    [Fact]
    public void First_snapshot_is_silent() =>
        Assert.Empty(new AlertEngine(_ => new()).Observe([Snap(running: false)], At(0)));

    [Fact]
    public void Stop_is_critical_and_immediate()
    {
        var engine = new AlertEngine(_ => new());
        engine.Observe([Snap()], At(0));
        var events = engine.Observe([Snap(running: false)], At(0.5));
        Assert.Equal(Severity.Critical, Assert.Single(events).Severity);
    }

    [Fact]
    public void Overheat_needs_two_fresh_updates()
    {
        var engine = new AlertEngine(_ => new());
        engine.Observe([Snap()], At(0));
        var hot = Snap(temp: 85, uptime: 1);
        Assert.Empty(engine.Observe([hot], At(2)));
        Assert.Empty(engine.Observe([hot], At(4))); // same data polled again: not a new update
        Assert.Contains(engine.Observe([Snap(temp: 86, uptime: 11)], At(12)), e => e.Message.StartsWith("Перегрев"));
    }

    [Fact]
    public void Per_resource_thresholds_apply()
    {
        var engine = new AlertEngine(id => id == "qemu/100" ? new Thresholds(CpuPct: 50) : new());
        engine.Observe([Snap(cpu: 0.6)], At(0));
        Assert.Contains(engine.Observe([Snap(cpu: 0.61)], At(1)), e => e.Message.Contains("порог 50%"));
    }

    [Fact]
    public void Missing_resource_is_reported()
    {
        var engine = new AlertEngine(_ => new());
        engine.Observe([Snap()], At(0));
        Assert.Contains(engine.Observe([], At(1)), e => e.Message == "Пропал из кластера");
    }

    [Fact]
    public void Settings_round_trip_and_broken_file_is_kept()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var s = AppSettings.Load(path);
            s.Tabs.Add("Прод");
            s.TabOf["demo:qemu/100"] = "Прод";
            s.Save();
            Assert.Equal("Прод", AppSettings.Load(path).TabOf["demo:qemu/100"]);

            File.WriteAllText(path, "{ broken");
            Assert.Empty(AppSettings.Load(path).Tabs);
            Assert.True(File.Exists(path + ".broken"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".broken");
        }
    }
}
