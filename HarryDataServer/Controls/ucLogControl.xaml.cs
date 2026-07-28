using System.Windows;
using System.Windows.Controls;

namespace HarryDataServer.Controls;

/// <summary>
/// Log tab (level/source filters, colour coding, export). DataContext is the LogViewModel.
///
/// <para>The console auto-scroll (follow the tail, pause on scroll-up/click, "▼ n new" overlay, keeping
/// the selected line across the per-tick rebuild) lives in <see cref="TailScrollView"/>, which wraps the
/// list in the XAML — there is no scroll code here.</para>
/// </summary>
public partial class ucLogControl : UserControl
{
    public ucLogControl() => InitializeComponent();

    /// <summary>"Copy line" context menu — delegates to the wrapping <see cref="TailScrollView"/>.</summary>
    private void OnCopyLine(object sender, RoutedEventArgs e) => TailScrollView.CopyLineFrom(sender);
}
