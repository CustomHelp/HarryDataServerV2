using System.Windows.Controls;

namespace HarryDataServer.Controls;

/// <summary>One SPS channel control (connected LED, last 2 requests/responses, counter).
/// DataContext is a SpsChannelViewModel.</summary>
public partial class ucSpsChannelControl : UserControl
{
    public ucSpsChannelControl() => InitializeComponent();
}
