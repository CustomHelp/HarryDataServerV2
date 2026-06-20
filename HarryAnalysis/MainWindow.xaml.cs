using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HarryAnalysis;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ScanBox.Focus();
        // Right-click selects the row under the cursor so the context menu acts on it.
        HistoryGrid.PreviewMouseRightButtonDown += OnHistoryRightClick;
    }

    private static void OnHistoryRightClick(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep is not null and not DataGridRow)
            dep = VisualTreeHelper.GetParent(dep);
        if (dep is DataGridRow row)
            row.IsSelected = true;
    }
}
