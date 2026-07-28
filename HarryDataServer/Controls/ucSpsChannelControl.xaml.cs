using System.Windows;
using System.Windows.Controls;

namespace HarryDataServer.Controls;

/// <summary>
/// One PLC channel card (connected LED, last requests/responses, counter).
/// DataContext is a SpsChannelViewModel.
///
/// <para>Both lists are wrapped in a <see cref="TailScrollView"/>: they show the oldest line at the top
/// and the newest at the bottom, follow the tail automatically, pause when the operator scrolls up or
/// clicks a line, and offer a "▼ n new" overlay to get back.</para>
/// </summary>
public partial class ucSpsChannelControl : UserControl
{
    public ucSpsChannelControl() => InitializeComponent();

    /// <summary>"Copy line" context menu — delegates to the wrapping <see cref="TailScrollView"/>.</summary>
    private void OnCopyLine(object sender, RoutedEventArgs e) => TailScrollView.CopyLineFrom(sender);
}
