using System.Windows.Controls;

namespace HarryDataServer.Controls;

/// <summary>Log tab (level/source filters, colour coding, export). DataContext is the LogViewModel.</summary>
public partial class ucLogControl : UserControl
{
    private bool _autoScroll = true;   // stick to the newest (bottom) until the user scrolls up
    private double _savedOffset;        // last user-chosen offset, restored across per-tick rebuilds

    public ucLogControl() => InitializeComponent();

    /// <summary>
    /// Console/chat auto-scroll: follow the newest entry only while the view is at the bottom.
    /// When the user has scrolled up, hold their position (the list is fully rebuilt every tick,
    /// so the previous offset is re-applied); auto-scroll resumes once they scroll back to the bottom.
    /// </summary>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer sv)
            return;

        if (e.ExtentHeightChange == 0)
        {
            // Pure user scroll (content unchanged): remember the position and whether it's the bottom.
            _savedOffset = sv.VerticalOffset;
            _autoScroll = sv.VerticalOffset >= sv.ScrollableHeight - 1.0;
        }
        else if (_autoScroll)
        {
            // Content grew/shrank while following the tail → snap to the newest.
            sv.ScrollToVerticalOffset(sv.ScrollableHeight);
        }
        else
        {
            // Content changed while the user was reading higher up → keep them where they were.
            sv.ScrollToVerticalOffset(_savedOffset);
        }
    }
}
