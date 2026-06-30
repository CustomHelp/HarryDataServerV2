using System.Windows;
using System.Windows.Controls;
using HarryDataServer.ViewModels;

namespace HarryDataServer.Controls;

/// <summary>One camera control (LEDs, telegram counter, last 3 telegrams, reconnect).
/// DataContext is a CameraViewModel.</summary>
public partial class ucCameraControl : UserControl
{
    public ucCameraControl() => InitializeComponent();

    /// <summary>
    /// Context-menu "Seriennummer kopieren": copy only the 22-char Serial1 of the right-clicked
    /// telegram line to the clipboard (no timestamp / OK-NG). The MenuItem inherits the line's
    /// <see cref="RecentTelegram"/> as its DataContext from the ListBoxItem.
    /// </summary>
    private void OnCopySerial(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: RecentTelegram line } && !string.IsNullOrEmpty(line.Serial))
        {
            try { Clipboard.SetText(line.Serial); }
            catch { /* clipboard may be momentarily locked by another process; ignore */ }
        }
    }
}
