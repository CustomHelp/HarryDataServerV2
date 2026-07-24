using System.Windows.Controls;

namespace HarryDataServer.Controls;

/// <summary>System resources control (CPU, RAM, server + mysqld load). DataContext is the MainViewModel.</summary>
public partial class ucSystemControl : UserControl
{
    public ucSystemControl() => InitializeComponent();
}
