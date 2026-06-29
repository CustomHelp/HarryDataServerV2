using System.Collections.Specialized;
using System.Windows;
using HarryShared.Theming;

namespace HarryGraph;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.Initialize();
        UpdateThemeButton();
        Loaded += OnLoaded;
        SizeChanged += (_, _) => QueueLayout();
        StateChanged += (_, _) => QueueLayout();
        PanelScroller.SizeChanged += (_, _) => LayoutPanels();
    }

    /// <summary>Recompute after the layout pass so the ScrollViewer viewport is up to date
    /// (e.g. right after the window is maximized/restored).</summary>
    private void QueueLayout() =>
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(LayoutPanels));

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

    private void OnThemeToggle(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        UpdateThemeButton();
    }

    private void UpdateThemeButton() =>
        ThemeToggle.Content = ThemeManager.Current == AppTheme.Dark ? "☀ Light" : "🌙 Dark";

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
