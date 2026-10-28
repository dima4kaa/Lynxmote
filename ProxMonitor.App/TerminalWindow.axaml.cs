using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ProxMonitor.Core;

namespace ProxMonitor.App;

/// <summary>One command and its answer, shown as a card.</summary>
public partial class TerminalBlock(string command) : ObservableObject
{
    public static readonly FuncValueConverter<bool, IBrush> FooterBrush =
        new(failed => new SolidColorBrush(Color.Parse(failed ? "#F87171" : "#6B7280")));

    public string Command { get; } = command;
    [ObservableProperty] private string _output = "";
    [ObservableProperty] private string _footer = "выполняется…";
    [ObservableProperty] private bool _failed;
}

public partial class TerminalWindow : Window
{
    readonly ResourceRow? _row;
    readonly MainViewModel? _vm;
    readonly ObservableCollection<TerminalBlock> _blocks = [];
    readonly List<string> _history = [];
    ITerminal? _terminal;
    CancellationTokenSource? _running;
    int _historyPos;
    string? _host, _seenHostKey;

    public TerminalWindow() => InitializeComponent();

    public TerminalWindow(ResourceRow row, MainViewModel vm) : this()
    {
        (_row, _vm) = (row, vm);
        Title = $"Терминал: {row.Name}";
        TargetText.Text = row.Name;
        Blocks.ItemsSource = _blocks;
        string[] common = ["uptime", "df -h", "free -h", "ip -br a", "systemctl --failed", "journalctl -n 30 --no-pager"];
        Chips.ItemsSource = row.IsNode ? ["pveversion", "qm list", "pct list", .. common] : common;
        Opened += async (_, _) => await StartAsync();
        Closed += (_, _) =>
        {
            _running?.Cancel();
            _terminal?.Dispose();
        };
    }

    async Task StartAsync()
    {
        if (_vm!.SourceOf(_row!) is DemoSource)
        {
            Connected(new DemoTerminal(_row!.IsNode ? _row.Name : _row.Name.Split(' ').Last()), "демо-режим");
            return;
        }
        if (_vm.SourceOf(_row!) is not ProxmoxSource source || _vm.ConfigOf(_row!) is not { } cfg)
        {
            StatusText.Text = "сервер не подключён";
            return;
        }
        StatusText.Text = "поиск адреса ноды…";
        _host = await source.NodeAddressAsync(_row!.Node, default);
        LoginHint.Text = $"Команды выполняются на ноде {_row.Node} ({_host})" +
                         (_row.IsNode ? "." : _row.LocalId.StartsWith("lxc/") ? " внутри контейнера через pct exec." : " внутри VM через qemu-guest-agent.");
        if (cfg.SshUser is { } user && Secrets.Unprotect(cfg.ProtectedSshPassword) is { } password)
        {
            UserBox.Text = user;
            PasswordBox.Text = password;
            await ConnectAsync(cfg);
        }
        else
        {
            ShowLogin();
        }
    }

    async Task ConnectAsync(ServerConfig cfg)
    {
        var user = UserBox.Text?.Trim() ?? "";
        var password = PasswordBox.Text ?? "";
        if (TrustBox.IsChecked == true && _seenHostKey is not null) cfg.SshHostKeys[_host!] = _seenHostKey;

        LoginButton.IsEnabled = false;
        StatusText.Text = "подключение…";
        var (target, vmid) = _row!.IsNode ? (TerminalTarget.Node, 0)
            : (_row.LocalId.StartsWith("lxc/") ? TerminalTarget.Container : TerminalTarget.Vm, int.Parse(_row.LocalId.Split('/')[1]));
        var result = await SshTerminal.ConnectAsync(_host!, user, password, cfg.SshHostKeys.GetValueOrDefault(_host!),
            _row.IsNode ? _row.Node : _row.Name.Split(' ').Last(), target, vmid, default);
        LoginButton.IsEnabled = true;

        if (result.Terminal is { } terminal)
        {
            cfg.SshUser = user;
            cfg.ProtectedSshPassword = Secrets.Protect(password);
            _vm!.SaveSettings();
            Connected(terminal, $"ssh {user}@{_host}");
            return;
        }
        ShowLogin();
        if (result.UnknownHostKey is { } key)
        {
            _seenHostKey = key;
            HostKeyText.Text = "SHA256:" + key;
            TrustBox.IsChecked = false;
            HostKeyPanel.IsVisible = true;
            LoginError.Text = cfg.SshHostKeys.ContainsKey(_host!)
                ? "Ключ сервера изменился! Если вы не переустанавливали сервер, не подключайтесь."
                : "Подтвердите отпечаток и нажмите «Подключиться» ещё раз";
        }
        else
        {
            LoginError.Text = result.Error;
        }
    }

    void ShowLogin()
    {
        LoginPanel.IsVisible = true;
        StatusText.Text = "нужен вход";
        PasswordBox.Focus();
    }

    void Connected(ITerminal terminal, string status)
    {
        _terminal = terminal;
        LoginPanel.IsVisible = false;
        StatusDot.Fill = new SolidColorBrush(Color.Parse("#4ADE80"));
        StatusText.Text = status;
        PromptText.Text = terminal.Prompt;
        Input.IsEnabled = true;
        Input.Focus();
    }

    async void Login_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm?.ConfigOf(_row!) is { } cfg) await ConnectAsync(cfg);
    }

    void Password_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Login_Click(sender, e);
    }

    // ---- running commands ----

    async Task RunAsync(string command)
    {
        command = command.Trim();
        if (_terminal is null || _running is not null || command.Length == 0) return;
        if (command == "clear")
        {
            _blocks.Clear();
            return;
        }
        if (_history.LastOrDefault() != command) _history.Add(command);
        _historyPos = _history.Count;

        var block = new TerminalBlock(command);
        _blocks.Add(block);
        Input.Text = "";
        StopButton.IsVisible = true;
        _running = new CancellationTokenSource();
        ScrollDown();
        try
        {
            var r = await _terminal.RunAsync(command, _running.Token);
            block.Output = r.Output;
            block.Failed = r.ExitCode != 0;
            block.Footer = $"{(r.ExitCode == 0 ? "готово" : $"код {r.ExitCode}")} · {r.Duration.TotalSeconds:0.00} с";
        }
        catch (OperationCanceledException)
        {
            (block.Footer, block.Failed) = ("прервано", true);
        }
        catch (Exception e)
        {
            (block.Output, block.Footer, block.Failed) = (e.Message, "ошибка", true);
        }
        finally
        {
            _running.Dispose();
            _running = null;
            StopButton.IsVisible = false;
            PromptText.Text = _terminal.Prompt;
            Input.Focus();
            ScrollDown();
        }
    }

    void ScrollDown() => Avalonia.Threading.Dispatcher.UIThread.Post(Scroll.ScrollToEnd, Avalonia.Threading.DispatcherPriority.Background);

    async void Input_KeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await RunAsync(Input.Text ?? "");
                break;
            case Key.Up when _history.Count > 0:
                e.Handled = true;
                _historyPos = Math.Max(0, _historyPos - 1);
                SetInput(_history[_historyPos]);
                break;
            case Key.Down when _history.Count > 0:
                e.Handled = true;
                _historyPos = Math.Min(_history.Count, _historyPos + 1);
                SetInput(_historyPos < _history.Count ? _history[_historyPos] : "");
                break;
            case Key.L when ctrl:
                e.Handled = true;
                _blocks.Clear();
                break;
            case Key.C when ctrl && _running is not null && string.IsNullOrEmpty(Input.SelectedText):
                e.Handled = true;
                _running.Cancel();
                break;
        }
    }

    void SetInput(string text)
    {
        Input.Text = text;
        Input.CaretIndex = text.Length;
    }

    async void Chip_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Content is string command) await RunAsync(command);
    }

    void Clear_Click(object? sender, RoutedEventArgs e) => _blocks.Clear();

    void Stop_Click(object? sender, RoutedEventArgs e) => _running?.Cancel();
}
