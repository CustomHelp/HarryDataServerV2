using System.Collections.Specialized;
using System.Windows;

namespace HarryGraph;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += (_, _) => LayoutPanels();
        PanelScroller.SizeChanged += (_, _) => LayoutPanels();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.MaximizeRequested += OnMaximizeRequested;
            vm.Panels.CollectionChanged += OnPanelsChanged;
        }
        LayoutPanels();
    }

    private void OnPanelsChanged(object? sender, NotifyCollectionChangedEventArgs e) => LayoutPanels();

    private static void OnMaximizeRequested(GraphPanelViewModel panel)
    {
        var clone = panel.CloneForWindow();
        new GraphWindow(clone).Show();
    }

    /// <summary>Size each panel so the graphs share the available area proportionally.</summary>
    private void LayoutPanels()
    {
        if (DataContext is not MainViewModel vm || vm.Panels.Count == 0)
            return;

        var count = vm.Panels.Count;
        var cols = (int)Math.Ceiling(Math.Sqrt(count));
        var rows = (int)Math.Ceiling((double)count / cols);

        var available = PanelScroller.ViewportWidth > 0 ? PanelScroller.ViewportWidth : ActualWidth;
        var availableH = PanelScroller.ViewportHeight > 0 ? PanelScroller.ViewportHeight : ActualHeight - 160;

        // Account for the 4px margin on each side of every panel control.
        var w = Math.Max(360, available / cols - 12);
        var h = Math.Max(260, availableH / rows - 12);

        foreach (var panel in vm.Panels)
        {
            panel.PanelWidth = w;
            panel.PanelHeight = h;
        }
    }
}
