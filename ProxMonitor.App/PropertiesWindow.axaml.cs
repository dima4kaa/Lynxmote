using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ProxMonitor.Core;

namespace ProxMonitor.App;

public partial class PropertiesWindow : Window
{
    readonly ResourceRow? _row;
    readonly MainViewModel? _vm;
    bool _loading = true;

    public PropertiesWindow()
    {
        InitializeComponent();
        this.UseOwnTitleBar(TitleBar, captionButtons: true);
    }

    public PropertiesWindow(ResourceRow row, MainViewModel vm) : this()
    {
        (_row, _vm) = (row, vm);
        DataContext = row;
        var events = new ObservableCollection<AlertEvent>(vm.Events.Where(e => e.Id == row.Key));
        EventsList.ItemsSource = events;
        NoEvents.IsVisible = events.Count == 0;

        void OnAlert(AlertEvent e)
        {
            if (e.Id != row.Key) return;
            events.Insert(0, e);
            NoEvents.IsVisible = false;
        }
        vm.Alert += OnAlert;
        Closed += (_, _) => vm.Alert -= OnAlert;
        Closing += (_, _) => Thresholds_Changed(null, new RoutedEventArgs()); // digits typed but not yet committed

        OwnBox.IsChecked = vm.HasOwnThresholds(row.Key);
        ShowThresholds(vm.ThresholdsFor(row.Key));
        _loading = false;
    }

    void ShowThresholds(Thresholds t)
    {
        (CpuBox.Value, TempBox.Value, DiskBox.Value) = ((decimal)t.CpuPct, (decimal)t.TempC, (decimal)t.DiskPct);
        ThresholdsBoxes.IsEnabled = OwnBox.IsChecked == true;
        ThresholdsHint.Text = OwnBox.IsChecked == true ? "Действуют только для этого сервера" : "Сейчас действуют общие значения из настроек";
    }

    void Thresholds_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading || _row is null || _vm is null) return;
        _loading = true;
        if (OwnBox.IsChecked == true)
            _vm.SetThresholds(_row.Key, SettingsWindow.Read(CpuBox, TempBox, DiskBox));
        else
            _vm.SetThresholds(_row.Key, null);
        ShowThresholds(_vm.ThresholdsFor(_row.Key));
        _loading = false;
    }

    void OpenTerminal(object? sender, RoutedEventArgs e)
    {
        if (_row is not null && _vm is not null) new TerminalWindow(_row, _vm).Show(this);
    }
}
