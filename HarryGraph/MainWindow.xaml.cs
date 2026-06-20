using System.Windows;
using System.Windows.Controls;

namespace HarryGraph;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>Print the current plot via the standard print dialog (fit to page).</summary>
    private void OnPrint(object sender, RoutedEventArgs e)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true)
            return;

        var area = new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);

        // Snapshot the plot's current size, lay it out to the page, print, then restore.
        var original = new Size(Plot.ActualWidth, Plot.ActualHeight);
        Plot.Measure(area);
        Plot.Arrange(new Rect(area));
        dialog.PrintVisual(Plot, "HarryGraph");

        // Restore the on-screen layout.
        Plot.Measure(original);
        Plot.Arrange(new Rect(original));
        Plot.UpdateLayout();
    }
}
