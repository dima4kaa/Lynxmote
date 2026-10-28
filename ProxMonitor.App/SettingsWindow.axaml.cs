using Avalonia.Controls;
using Avalonia.Interactivity;
using ProxMonitor.Core;

namespace ProxMonitor.App;

public partial class SettingsWindow : Window
{
    readonly MainViewModel? _vm;

    public SettingsWindow()
    {
        InitializeComponent();
        this.UseOwnTitleBar(this, captionButtons: false);
    }

    public SettingsWindow(MainViewModel vm) : this()
    {
        _vm = vm;
        var t = vm.GlobalThresholds;
        (CpuBox.Value, TempBox.Value, DiskBox.Value) = ((decimal)t.CpuPct, (decimal)t.TempC, (decimal)t.DiskPct);
    }

    void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    void Save_Click(object? sender, RoutedEventArgs e)
    {
        _vm?.SetGlobalThresholds(Read(CpuBox, TempBox, DiskBox));
        Close();
    }

    /// <summary>Reads the boxes' text: NumericUpDown only commits typed digits on Enter or focus loss.</summary>
    public static Thresholds Read(NumericUpDown cpu, NumericUpDown temp, NumericUpDown disk)
    {
        static double Of(NumericUpDown b) =>
            (double)Math.Clamp(decimal.TryParse(b.Text, out var v) ? v : b.Value ?? b.Minimum, b.Minimum, b.Maximum);
        return new Thresholds(Of(cpu), Of(temp), Of(disk));
    }
}
