using System.Collections.ObjectModel;
using Avalonia.Controls;
using ProxMonitor.Core;

namespace ProxMonitor.App;

public partial class DetailsWindow : Window
{
    public DetailsWindow() => InitializeComponent();

    public DetailsWindow(ResourceRow row, MainViewModel vm) : this()
    {
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
    }
}
