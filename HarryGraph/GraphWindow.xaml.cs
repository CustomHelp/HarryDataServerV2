using System.Windows;
using System.Windows.Threading;

namespace HarryGraph;

/// <summary>Full-screen detail view hosting one graph panel (its own live timer).</summary>
public partial class GraphWindow : Window
{
    private readonly GraphPanelViewModel _vm;
    private readonly DispatcherTimer _timer;

    public GraphWindow(GraphPanelViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        Host.DataContext = vm;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => { if (_vm.LiveMode) await _vm.RefreshAsync(); };
        _timer.Start();

        Closed += (_, _) => _timer.Stop();
    }
}
