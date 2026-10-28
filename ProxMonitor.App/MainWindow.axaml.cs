using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using ProxMonitor.Core;

namespace ProxMonitor.App;

public partial class MainWindow : Window
{
    WindowNotificationManager? _toasts;

    public MainWindow()
    {
        InitializeComponent();
        // Right-click acts on the row under the cursor, like in a spreadsheet.
        ResourceGrid.AddHandler(PointerPressedEvent, SelectRowOnRightClick, RoutingStrategies.Tunnel);
    }

    MainViewModel Vm => (MainViewModel)DataContext!;

    List<ResourceRow> SelectedRows => ResourceGrid.SelectedItems.OfType<ResourceRow>().ToList();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _toasts = new WindowNotificationManager(this) { Position = NotificationPosition.BottomRight, MaxItems = 4, Margin = new Thickness(0, 0, 8, 48) };
        Vm.Alert += ShowToast;
    }

    void ShowToast(AlertEvent e)
    {
        if (e.Severity == Severity.Info) return; // info goes to the log only
        var type = e.Severity == Severity.Critical ? NotificationType.Error : NotificationType.Warning;
        _toasts?.Show(new Notification(e.ResourceName, e.Message, type));
    }

    // ---- title bar ----

    void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            BeginMoveDrag(e);
    }

    void ToggleTheme(object? sender, RoutedEventArgs e)
    {
        var toLight = ActualThemeVariant == ThemeVariant.Dark;
        Application.Current!.RequestedThemeVariant = toLight ? ThemeVariant.Light : ThemeVariant.Dark;
        Vm.SaveTheme(toLight ? "Light" : "Dark");
    }

    void OpenSettings(object? sender, RoutedEventArgs e) => new SettingsWindow(Vm).ShowDialog(this);

    async void ConnectServer(object? sender, RoutedEventArgs e)
    {
        if (await new ConnectWindow().ShowDialog<NewServer?>(this) is { } server)
            Vm.AddServer(server.Config, server.Secret);
    }

    void OpenDemo(object? sender, RoutedEventArgs e) => Vm.EnableDemo();

    // ---- table ----

    void SelectRowOnRightClick(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ResourceGrid).Properties.IsRightButtonPressed) return;
        if ((e.Source as StyledElement)?.DataContext is ResourceRow row && !ResourceGrid.SelectedItems.Contains(row))
            ResourceGrid.SelectedItem = row;
    }

    void Grid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as StyledElement)?.DataContext is ResourceRow row) OpenProperties(row);
    }

    void RowMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var rows = SelectedRows;
        if (rows.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        // Power actions only make sense for guests, and only the ones that change their state.
        StartItem.IsVisible = Guests(running: false).Count > 0;
        StopItem.IsVisible = RebootItem.IsVisible = Guests(running: true).Count > 0;
        SnapshotItem.IsVisible = PowerSeparator.IsVisible = rows.Any(r => !r.IsNode);

        SectionMenu.Items.Clear();
        foreach (var section in Vm.Sections)
            SectionMenu.Items.Add(Item(section, () => Vm.MoveToSection(rows, section)));
        if (SectionMenu.Items.Count > 0) SectionMenu.Items.Add(new Separator());
        SectionMenu.Items.Add(Item("Новый раздел…", async () =>
        {
            if (await PromptAsync("Новый раздел", "Например, Продакшн") is { } name) Vm.MoveToSection(rows, name);
        }));
        SectionMenu.Items.Add(Item("Убрать из раздела", () => Vm.MoveToSection(rows, null)));

        MoveMenu.Items.Clear();
        foreach (var tab in Vm.Tabs.Where(t => !t.IsAll))
            MoveMenu.Items.Add(Item(tab.Name, () => Vm.MoveToTab(rows, tab)));
        if (MoveMenu.Items.Count == 0)
            MoveMenu.Items.Add(new MenuItem { Header = "Сначала создайте вкладку кнопкой + внизу", IsEnabled = false });
        MoveMenu.Items.Add(new Separator());
        MoveMenu.Items.Add(Item("Убрать из вкладки", () => Vm.MoveToTab(rows, null)));
    }

    void Details_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedRows.FirstOrDefault() is { } row) OpenProperties(row);
    }

    void OpenProperties(ResourceRow row) => new PropertiesWindow(row, Vm).Show(this);

    void AddWidget_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var row in SelectedRows) Vm.AddWidget(row);
    }

    List<ResourceRow> Guests(bool running) => SelectedRows.Where(r => !r.IsNode && r.Running == running).ToList();

    async void Start_Click(object? sender, RoutedEventArgs e) => await Vm.RunAsync(PowerAction.Start, Guests(running: false));
    async void Snapshot_Click(object? sender, RoutedEventArgs e) => await Vm.RunAsync(PowerAction.Snapshot, SelectedRows.Where(r => !r.IsNode).ToList());
    async void Stop_Click(object? sender, RoutedEventArgs e) => await ConfirmAndRun(PowerAction.Stop, "Остановить");
    async void Reboot_Click(object? sender, RoutedEventArgs e) => await ConfirmAndRun(PowerAction.Reboot, "Перезагрузить");

    async Task ConfirmAndRun(PowerAction action, string verb)
    {
        var rows = Guests(running: true);
        if (rows.Count == 0) return;
        var what = rows.Count == 1 ? rows[0].Name : $"{rows.Count} шт.";
        if (await ConfirmAsync($"{verb} {what}?", "Несохранённые данные внутри гостевой системы могут быть потеряны.", verb))
            await Vm.RunAsync(action, rows);
    }

    // ---- widgets ----

    WidgetVm? _dragged;
    Point _dragStart, _dragOrigin;

    void Widget_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: WidgetVm w } c || !e.GetCurrentPoint(c).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2 && w.Row is { } row)
        {
            OpenProperties(row);
            return;
        }
        _dragged = w;
        _dragStart = e.GetPosition(WidgetLayer);
        _dragOrigin = new Point(w.X, w.Y);
        e.Pointer.Capture(c);
        e.Handled = true;
    }

    void Widget_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragged is null || sender is not Control c) return;
        var p = e.GetPosition(WidgetLayer);
        // Keep the card inside the table area.
        _dragged.X = Math.Clamp(_dragOrigin.X + p.X - _dragStart.X, 0, Math.Max(0, WidgetLayer.Bounds.Width - c.Bounds.Width));
        _dragged.Y = Math.Clamp(_dragOrigin.Y + p.Y - _dragStart.Y, 0, Math.Max(0, WidgetLayer.Bounds.Height - c.Bounds.Height));
    }

    void Widget_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragged is null) return;
        _dragged = null;
        e.Pointer.Capture(null);
        Vm.SaveSettings();
    }

    void Widget_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: WidgetVm w } c) return;
        e.Handled = true;
        MenuItem Toggle(string header, bool on, Action<bool> set)
        {
            var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = on };
            item.Click += (_, _) =>
            {
                set(!on);
                Vm.SaveSettings();
            };
            return item;
        }
        var menu = new List<Control>
        {
            Toggle("CPU", w.Cpu, v => w.Cpu = v),
            Toggle("RAM", w.Ram, v => w.Ram = v),
            Toggle("Диск", w.Disk, v => w.Disk = v),
            Toggle("Температура", w.Temp, v => w.Temp = v),
            Toggle("Сеть", w.Net, v => w.Net = v),
            new Separator(),
            Item("Удалить виджет", () => Vm.RemoveWidget(w)),
        };
        if (w.Row is { } row)
        {
            menu.Add(new Separator());
            menu.Add(Item("Свойства", () => OpenProperties(row)));
        }
        new ContextMenu { ItemsSource = menu }.Open(c);
    }

    // ---- tabs and servers ----

    void CreateTab_Click(object? sender, RoutedEventArgs e)
    {
        Vm.CreateTabCommand.Execute(null);
        NewTabButton.Flyout?.Hide();
    }

    void NewTab_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CreateTab_Click(sender, e);
    }

    void Tabs_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if ((e.Source as StyledElement)?.DataContext is not SheetTab { IsAll: false } tab) return;
        e.Handled = true;
        OpenMenu((Control)e.Source!, Item("Удалить вкладку", () => Vm.DeleteTab(tab)));
    }

    void Sources_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if ((e.Source as StyledElement)?.DataContext is not SourceState source) return;
        e.Handled = true;
        OpenMenu((Control)e.Source!, Item("Отключить сервер", async () =>
        {
            if (await ConfirmAsync($"Отключить «{source.Title}»?", "Сервер пропадёт из таблицы, токен будет удалён.", "Отключить"))
                Vm.RemoveSource(source);
        }));
    }

    // ---- helpers ----

    static MenuItem Item(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    static void OpenMenu(Control target, params MenuItem[] items) =>
        new ContextMenu { ItemsSource = items }.Open(target);

    Task<bool> ConfirmAsync(string title, string text, string ok)
    {
        var yes = new Button { Content = ok, Padding = new Thickness(16, 6) };
        yes.Classes.Add("accent");
        var no = new Button { Content = "Отмена", Padding = new Thickness(16, 6) };
        var dialog = Dialog(no, title, new TextBlock { Text = text, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap }, no, yes);
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        return dialog.ShowDialog<bool>(this);
    }

    async Task<string?> PromptAsync(string title, string watermark)
    {
        var box = new TextBox { Watermark = watermark };
        var ok = new Button { Content = "Создать", Padding = new Thickness(16, 6), IsDefault = true };
        ok.Classes.Add("accent");
        var cancel = new Button { Content = "Отмена", Padding = new Thickness(16, 6) };
        var dialog = Dialog(box, title, box, cancel, ok);
        ok.Click += (_, _) => dialog.Close(box.Text?.Trim());
        cancel.Click += (_, _) => dialog.Close(null);
        return await dialog.ShowDialog<string?>(this) is { Length: > 0 } name ? name : null;
    }

    /// <summary>Small modal window: title, body, buttons on the right; Esc closes it.</summary>
    static Window Dialog(Control focus, string title, Control body, params Button[] buttons)
    {
        var dialog = new Window
        {
            Title = "ProxMonitor", Width = 400, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Thickness(24), Spacing = 10,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                    body,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8, Margin = new Thickness(0, 10, 0, 0), Children = { buttons[0], buttons[1] },
                    },
                },
            },
        };
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) dialog.Close();
        };
        // Often opened from a context menu, which hands activation back to the main window as it closes.
        dialog.Opened += (_, _) =>
        {
            dialog.Activate();
            focus.Focus();
        };
        return dialog;
    }
}
