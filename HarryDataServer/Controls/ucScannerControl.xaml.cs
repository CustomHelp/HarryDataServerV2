using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HarryDataServer.ViewModels;

namespace HarryDataServer.Controls;

/// <summary>
/// Scanner tab: a live grid of the last N DMC scans with a right-click "Copy Code" / "Copy Row"
/// context menu. Right-clicking a row selects it first so the menu acts on the row under the cursor
/// (same idiom as the companion tools' history grids).
/// </summary>
public partial class ucScannerControl : UserControl
{
    public ucScannerControl()
    {
        InitializeComponent();
        ScansGrid.PreviewMouseRightButtonDown += OnRowRightClick;
    }

    private static void OnRowRightClick(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep is not null and not DataGridRow)
            dep = VisualTreeHelper.GetParent(dep);
        if (dep is DataGridRow row)
            row.IsSelected = true;
    }

    private void OnCopyCode(object sender, RoutedEventArgs e)
    {
        if (ScansGrid.SelectedItem is ScanRow row)
            TrySetClipboard(row.Code);
    }

    private void OnCopyRow(object sender, RoutedEventArgs e)
    {
        if (ScansGrid.SelectedItem is ScanRow row)
            TrySetClipboard($"{row.Timestamp}\t{row.Code}");
    }

    private static void TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch { /* clipboard may be locked by another process — ignore */ }
    }
}
