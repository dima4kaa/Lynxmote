using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;

namespace ProxMonitor.App;

/// <summary>Replaces the Windows title bar with the app's own, as on the main window.</summary>
public static class Chrome
{
    /// <param name="dragArea">Where the window can be grabbed; clicks handled by controls inside it don't drag.</param>
    /// <param name="captionButtons">Keep minimize/maximize/close; dialogs drop them and close with their own buttons or Esc.</param>
    public static void UseOwnTitleBar(this Window w, Control dragArea, bool captionButtons)
    {
        w.ExtendClientAreaToDecorationsHint = true;
        w.ExtendClientAreaChromeHints = captionButtons ? ExtendClientAreaChromeHints.PreferSystemChrome : ExtendClientAreaChromeHints.NoChrome;
        w.ExtendClientAreaTitleBarHeightHint = captionButtons ? 40 : 0;
        if (!captionButtons && w.Content is Control content)
        {
            // Without the system frame a dialog's edge melts into the window behind it.
            var frame = new Border { BorderThickness = new Thickness(1), Child = null };
            w.Content = null;
            frame.Child = content;
            frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("Line"));
            w.Content = frame;
        }
        dragArea.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(w).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2 && w.CanResize)
                w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else
                w.BeginMoveDrag(e);
        };
    }
}
