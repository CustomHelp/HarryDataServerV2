using System.Windows.Controls;

namespace HarryDataServer.Controls;

/// <summary>Log tab (level/source filters, colour coding, export). DataContext is the LogViewModel.</summary>
public partial class ucLogControl : UserControl
{
    public ucLogControl() => InitializeComponent();
}
