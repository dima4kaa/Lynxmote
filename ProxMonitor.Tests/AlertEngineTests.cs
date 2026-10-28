using ProxMonitor.Core;

namespace ProxMonitor.Tests;

public class AlertEngineTests
{
    static readonly DateTime T0 = new(2026, 1, 1);
    static readonly TimeSpan Hold = TimeSpan.FromSeconds(1), Window = TimeSpan.FromMinutes(1);

    static ResourceSnapshot Snap(bool running = true, double cpu = 0.1, double? temp = 50, long diskUsed = 10) =>
        new("qemu/100", "100 · web", ResourceKind.Vm, "pve-01", running, cpu, 1, 2, diskUsed, 100, temp, 0, 0, 0, TimeSpan.Zero);

    static DateTime At(double seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void Continuous_danger_fires_after_one_second()
    {
        var check = new SustainedCheck(Hold, Window);
        Assert.False(check.Observe("k", true, At(0)));
        Assert.False(check.Observe("k", true, At(0.5)));
        Assert.True(check.Observe("k", true, At(1.0)));
        Assert.False(check.Observe("k", true, At(1.5))); // only once
    }

    [Fact]
    public void Scattered_danger_adds_up_within_a_minute()
    {
        var check = new SustainedCheck(Hold, Window);
        var fired = false;
        // 0.25 s of danger every 10 s: total reaches 1 s on the 4th blip.
        for (var t = 0.0; t < 40; t += 0.25)
            fired |= check.Observe("k", t % 10 == 0.25, At(t));
        Assert.True(fired);
    }

    [Fact]
    public void Danger_older_than_the_window_is_forgotten()
    {
        var check = new SustainedCheck(Hold, Window);
        Assert.False(check.Observe("k", false, At(0)));
        Assert.False(check.Observe("k", true, At(0.6)));
        Assert.False(check.Observe("k", false, At(70)));
        Assert.False(check.Observe("k", true, At(70.6)));
    }

    [Fact]
    public void Rearms_after_a_quiet_window()
    {
        var check = new SustainedCheck(Hold, Window);
        check.Observe("k", true, At(0));
        Assert.True(check.Observe("k", true, At(1)));
        Assert.False(check.Observe("k", false, At(62)));
        Assert.False(check.Observe("k", true, At(62.5)));
        Assert.True(check.Observe("k", true, At(63)));
    }

    [Fact]
    public void First_snapshot_is_silent() =>
        Assert.Empty(new AlertEngine(new()).Observe([Snap(running: false)], At(0)));

    [Fact]
    public void Stop_is_critical_and_immediate()
    {
        var engine = new AlertEngine(new());
        engine.Observe([Snap()], At(0));
        var events = engine.Observe([Snap(running: false)], At(0.5));
        Assert.Equal(Severity.Critical, Assert.Single(events).Severity);
    }

    [Fact]
    public void Overheat_needs_a_second()
    {
        var engine = new AlertEngine(new());
        engine.Observe([Snap()], At(0));
        Assert.Empty(engine.Observe([Snap(temp: 85)], At(0.5)));
        Assert.Contains(engine.Observe([Snap(temp: 85)], At(1.0)), e => e.Message.StartsWith("Перегрев"));
    }

    [Fact]
    public void Missing_resource_is_reported()
    {
        var engine = new AlertEngine(new());
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
