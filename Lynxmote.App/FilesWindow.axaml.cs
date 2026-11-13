using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Lynxmote.Core;

namespace Lynxmote.App;

/// <summary>A folder or file in the tree; a folder asks for its content the first time it is opened.</summary>
public partial class FileNode(string path, string name, bool isFolder, Func<FileNode, Task>? load = null) : ObservableObject
{
    public string Path { get; } = path;
    public string Name { get; } = name;
    public FontWeight Weight => isFolder ? FontWeight.SemiBold : FontWeight.Normal;
    public double Opacity => isFolder ? 1 : 0.75;
    /// <summary>A closed folder holds one stand-in child, so the tree draws its arrow.</summary>
    public ObservableCollection<FileNode> Children { get; } = isFolder ? [new("", "…", false)] : [];
    [ObservableProperty] private bool _isExpanded;
    bool _asked;

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || _asked || load is null) return;
        _asked = true;
        _ = load(this);
    }
}

/// <summary>The machine's folders as a tree, read with ls over the terminal's connection. Only looking, no actions.</summary>
public partial class FilesWindow : Window
{
    ITerminal? _terminal;
    bool _closed;

    public FilesWindow()
    {
        InitializeComponent();
        this.UseOwnTitleBar(TitleBar, captionButtons: true);
    }

    public FilesWindow(ResourceRow row, MainViewModel vm) : this()
    {
        Title = TitleText.Text = L.F("Файлы: {0}", row.Name);
        StatusText.Text = L.T("подключение…");
        Opened += async (_, _) =>
        {
            (_terminal, var problem) = await TerminalWindow.ConnectSavedAsync(row, vm);
            if (_closed) _terminal?.Dispose();
            if (_terminal is null)
            {
                StatusText.Text = problem;
                return;
            }
            StatusText.Text = L.T("Только просмотр: содержимое папки читается, когда вы её раскрываете.");
            var root = new FileNode("/", "/", true, LoadAsync);
            Tree.ItemsSource = new[] { root };
            root.IsExpanded = true;
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _terminal?.Dispose();
        };
    }

    async Task LoadAsync(FileNode folder)
    {
        IReadOnlyList<(string Name, bool IsFolder)> entries = [];
        string? note = null;
        try
        {
            var r = await _terminal!.RunAsync(Shell.ListCommand(folder.Path), default);
            // ls: 0 is fine, 1 means some entries can't be read, 2 the folder itself; other codes are ssh, pct or qm failing.
            if (r.ExitCode is 0 or 1) entries = Shell.ParseList(r.Output);
            else note = r.Output.Length > 0 ? r.Output : L.T("нет доступа");
        }
        catch (Exception e)
        {
            note = e.Message;
        }
        folder.Children.Clear();
        foreach (var (name, isFolder) in entries)
            folder.Children.Add(new FileNode(folder.Path.TrimEnd('/') + "/" + name, name, isFolder, LoadAsync));
        if (folder.Children.Count == 0) folder.Children.Add(new FileNode("", note ?? L.T("пусто"), false));
    }
}
