using System.Windows.Controls;

namespace HarryDataServer.Controls;

/// <summary>Database control (LEDs, live row counts, retention). DataContext is the MainViewModel.</summary>
public partial class ucDatabaseControl : UserControl
{
    public ucDatabaseControl() => InitializeComponent();
}
