using Avalonia.Data.Converters;
using Avalonia.Media;
using ProxMonitor.Core;

namespace ProxMonitor.App;

public static class Converters
{
    static readonly IBrush Green = new SolidColorBrush(Color.Parse("#22C55E"));
    static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#F59E0B"));
    static readonly IBrush Red = new SolidColorBrush(Color.Parse("#EF4444"));
    static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#5C7CFA"));
    static readonly IBrush Gray = new SolidColorBrush(Color.Parse("#8A90A0"));

    public static readonly FuncValueConverter<bool, IBrush> StatusBrush = new(ok => ok ? Green : Red);

    public static readonly FuncValueConverter<double, IBrush> LoadBrush =
        new(pct => pct >= 90 ? Red : pct >= 70 ? Amber : Accent);

    public static readonly FuncValueConverter<double?, IBrush> TempBrush =
        new(t => t is null ? Gray : t >= 80 ? Red : t >= 65 ? Amber : Green);

    public static readonly FuncValueConverter<Severity, IBrush> SeverityBrush = new(s => s switch
    {
        Severity.Critical => Red,
        Severity.Warning => Amber,
        _ => Accent,
    });

    public static readonly FuncValueConverter<bool, double> StaleOpacity = new(stale => stale ? 0.45 : 1);
}
