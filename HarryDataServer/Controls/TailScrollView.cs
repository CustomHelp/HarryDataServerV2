using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HarryDataServer.Controls;

/// <summary>
/// Reusable "console tail" scroll behaviour for any scrollable list (log tab, PLC channel cards).
/// Wrap the list in this control — nothing else is needed:
/// <code>
/// &lt;ctl:TailScrollView&gt;&lt;ListBox ItemsSource="{Binding Entries}" /&gt;&lt;/ctl:TailScrollView&gt;
/// </code>
///
/// <para><b>Behaviour</b></para>
/// <list type="number">
///   <item>At the bottom → new entries are followed automatically.</item>
///   <item>Scrolling up <b>or clicking into the list</b> pauses following: the view stays exactly where
///     it is while new entries keep piling up below it.</item>
///   <item>While paused a small overlay button "▼ n new" appears bottom-right (n = entries added since
///     the pause). Clicking it jumps to the end and resumes following.</item>
///   <item>Scrolling back to the bottom (with a small tolerance) resumes following too.</item>
///   <item>Nothing is ever scrolled while the mouse button is held down (drag-selecting) or while a
///     wrapped text control has an active selection — copying a line works while the log runs.</item>
/// </list>
///
/// <para><b>Pure view mechanics.</b> No view model is involved: the control listens to the bubbling
/// <see cref="ScrollViewer.ScrollChangedEvent"/> of the wrapped list and drives only the scroll offset.
/// Log content, ordering and persistence are untouched.</para>
///
/// <para><b>Why item keys.</b> Both consumers rebuild their collection on every UI tick
/// (<c>Clear()</c> + <c>Add()</c>), so item <i>references</i> are not stable and a raw pixel/index
/// offset would drift whenever the ring buffer drops an old entry. The control therefore anchors on the
/// <see cref="object.ToString"/> value of the topmost visible item and counts new entries relative to
/// the last item seen at pause time. If that anchor has itself been pushed out of the ring, every item
/// in the list is by definition newer, which is exactly what is then reported.</para>
/// </summary>
public class TailScrollView : ContentControl
{
    /// <summary>
    /// Distance from the end that still counts as "at the bottom". For a virtualizing list the scroll
    /// unit is items, so this is "within the last two entries"; for a pixel-scrolling one it is 2 px.
    /// </summary>
    private const double BottomTolerance = 2.0;

    private static readonly DependencyPropertyKey IsPausedPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(IsPaused), typeof(bool), typeof(TailScrollView),
            new PropertyMetadata(false));

    private static readonly DependencyPropertyKey NewCountPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(NewCount), typeof(int), typeof(TailScrollView),
            new PropertyMetadata(0));

    private static readonly DependencyPropertyKey IsJumpAvailablePropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(IsJumpAvailable), typeof(bool), typeof(TailScrollView),
            new PropertyMetadata(false));

    public static readonly DependencyProperty IsPausedProperty = IsPausedPropertyKey.DependencyProperty;
    public static readonly DependencyProperty NewCountProperty = NewCountPropertyKey.DependencyProperty;
    public static readonly DependencyProperty IsJumpAvailableProperty = IsJumpAvailablePropertyKey.DependencyProperty;

    private ScrollViewer? _scroll;
    private ItemsControl? _list;
    private ButtonBase? _jumpButton;
    private INotifyCollectionChanged? _observed;

    private bool _following = true;      // start at the tail, like a console
    private double _savedOffset;         // last position the user chose
    private int _anchorIndex = -1;       // index of the topmost visible item at that position
    private string? _anchorKey;          // ToString() of that item, to survive a rebuild
    private string? _pauseTailKey;       // ToString() of the newest item when following was paused
    private bool _mouseDown;             // a drag/selection is in progress → never scroll
    private bool _contentUpdating;       // a collection change is being processed → ignore scroll noise
    private string? _selectedKey;        // ToString() of the selected line, to survive a rebuild

    public TailScrollView()
    {
        // ScrollChanged bubbles out of the wrapped list, so the inner ScrollViewer needs no lookup.
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => EnsureList();
        SizeChanged += (_, _) => ScheduleContentUpdate();   // a viewport resize must keep the tail visible
    }

    /// <summary>True while following is paused (the user scrolled up or clicked into the list).</summary>
    public bool IsPaused => (bool)GetValue(IsPausedProperty);

    /// <summary>Entries added since following was paused.</summary>
    public int NewCount => (int)GetValue(NewCountProperty);

    /// <summary>True when the "▼ n new" overlay should be offered (paused AND something new arrived).</summary>
    public bool IsJumpAvailable => (bool)GetValue(IsJumpAvailableProperty);

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_jumpButton is not null)
            _jumpButton.Click -= OnJumpClick;

        _jumpButton = GetTemplateChild("PART_JumpButton") as ButtonBase;
        if (_jumpButton is not null)
            _jumpButton.Click += OnJumpClick;
    }

    /// <summary>Jump to the newest entry and resume following (the "▼ n new" action).</summary>
    public void ScrollToEnd()
    {
        Resume();
        EnsureList();
        _scroll ??= FindDescendant<ScrollViewer>(this);
        _scroll?.ScrollToVerticalOffset(_scroll.ScrollableHeight);
    }

    private void OnJumpClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;   // do not let the click bubble on as a "click into the list"
        ScrollToEnd();
    }

    // ---- content changes -----------------------------------------------------------------------

    /// <summary>
    /// Find the wrapped list and observe its item collection.
    ///
    /// <para><b>Why the collection and not <c>ScrollChanged</c>:</b> both consumers use a ring buffer, so
    /// once it is full the item COUNT no longer changes — <c>ExtentHeightChange</c> stays 0 and a
    /// scroll-event-based detection cannot see that the content moved on (the view would silently drift
    /// by the number of dropped entries and the counter would stay at 0).</para>
    /// </summary>
    private void EnsureList()
    {
        var list = FindDescendant<ItemsControl>(this);
        if (ReferenceEquals(list, _list))
            return;

        if (_observed is not null)
            _observed.CollectionChanged -= OnItemsChanged;
        if (_list is Selector oldSelector)
            oldSelector.SelectionChanged -= OnSelectionChanged;

        _list = list;
        _observed = _list?.Items as INotifyCollectionChanged;
        if (_observed is not null)
            _observed.CollectionChanged += OnItemsChanged;
        if (_list is Selector selector)
            selector.SelectionChanged += OnSelectionChanged;
    }

    /// <summary>
    /// Remember WHICH line the user picked. A rebuild clears the selection (the item objects are
    /// replaced), and that transient null must not erase the memory — only a real selection updates it.
    /// </summary>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is Selector selector && selector.SelectedItem is not null)
            _selectedKey = KeyOf(selector.SelectedItem);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleContentUpdate();

    /// <summary>
    /// Coalesce a burst of collection changes (a per-tick rebuild raises Reset + one Add per row) into a
    /// single update that runs AFTER the layout pass, so extents and offsets are already valid.
    /// </summary>
    private void ScheduleContentUpdate()
    {
        if (_contentUpdating)
            return;
        _contentUpdating = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ApplyContentUpdate));
    }

    private void ApplyContentUpdate()
    {
        try
        {
            var sv = _scroll ??= FindDescendant<ScrollViewer>(this);
            if (sv is null || ItemCount() == 0)
                return;

            // A rebuild recreates the item objects, so a selected line would silently vanish — restore it
            // by its text, otherwise the operator cannot select a line and copy it while the log runs.
            RestoreSelection();

            if (_following)
            {
                // Requirement 1: follow the newest entry — unless a selection/drag is active (req. 5).
                if (!IsSelectionActive())
                    sv.ScrollToVerticalOffset(sv.ScrollableHeight);
                return;
            }

            // Requirement 2/3: hold the position exactly and report what came in.
            UpdateNewCount();
            RestoreAnchor(sv);
        }
        finally
        {
            _contentUpdating = false;
        }
    }

    // ---- user scrolling ------------------------------------------------------------------------

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer sv)
            return;
        _scroll = sv;
        EnsureList();

        // While a content update is in flight the ScrollViewer emits its own noise (a Clear() resets the
        // offset to 0). That must never be mistaken for the user scrolling to the bottom.
        if (_contentUpdating)
            return;

        _savedOffset = sv.VerticalOffset;
        RememberAnchor(sv);

        if (sv.VerticalOffset >= sv.ScrollableHeight - BottomTolerance)
            Resume();          // requirement 4: back at the bottom → follow again
        else
            Pause();           // requirement 2: scrolled up → hold this position
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mouseDown = true;

        // A click on the overlay button means "take me to the end", not "pause here".
        if (_jumpButton is not null && e.OriginalSource is DependencyObject src && IsDescendantOf(src, _jumpButton))
            return;

        Pause();   // requirement 2: clicking into the list pauses following
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _mouseDown = false;

    // ---- selection survival + copying ----------------------------------------------------------

    /// <summary>
    /// Helper for a "Copy line" <see cref="MenuItem"/>: walks from the clicked menu item to the list it
    /// belongs to, up to the wrapping <see cref="TailScrollView"/>, and copies its selected line. Keeps
    /// the per-view code-behind to a single line.
    /// </summary>
    public static bool CopyLineFrom(object menuItemSender)
    {
        if (menuItemSender is not MenuItem item)
            return false;

        var placementTarget = (item.Parent as ContextMenu)?.PlacementTarget
                              ?? ItemsControl.ItemsControlFromItemContainer(item) as DependencyObject;
        for (var node = placementTarget; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is TailScrollView host)
                return host.CopySelectedLine();

        return false;
    }

    /// <summary>The text of the currently selected line, or null. Used by the copy affordances.</summary>
    public string? SelectedLineText()
    {
        EnsureList();
        return _list is Selector selector ? KeyOf(selector.SelectedItem) : null;
    }

    /// <summary>
    /// Copy the selected line to the clipboard (Ctrl+C, and the lists' "Copy line" context menu).
    /// Returns false when there is nothing selected or the clipboard is busy.
    /// </summary>
    public bool CopySelectedLine()
    {
        var text = SelectedLineText();
        if (string.IsNullOrEmpty(text))
            return false;

        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception)
        {
            // The clipboard is a shared OS resource and can be locked by another process for a moment.
            // Nothing to recover here and no health channel for a UI convenience — the operator simply
            // repeats the action. Deliberately not surfaced as an application fault.
            return false;
        }
    }

    /// <summary>Re-select the same LINE after a rebuild replaced the item objects.</summary>
    private void RestoreSelection()
    {
        if (_list is not Selector selector || selector.SelectedItem is not null || _selectedKey is null)
            return;

        var index = IndexOfKey(_selectedKey, -1);
        if (index >= 0)
            selector.SelectedIndex = index;
        else
            _selectedKey = null;   // the line has left the ring buffer
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            e.Handled = CopySelectedLine();
    }

    private void Pause()
    {
        if (!_following)
            return;

        _following = false;
        _pauseTailKey = KeyOf(ItemAt(ItemCount() - 1));
        SetValue(IsPausedPropertyKey, true);
        SetNewCount(0);
    }

    private void Resume()
    {
        _following = true;
        _pauseTailKey = null;
        SetValue(IsPausedPropertyKey, false);
        SetNewCount(0);
    }

    private void SetNewCount(int value)
    {
        SetValue(NewCountPropertyKey, value);
        SetValue(IsJumpAvailablePropertyKey, IsPaused && value > 0);
    }

    /// <summary>
    /// How many entries arrived since the pause: everything after the item that was newest back then.
    /// If that item has been pushed out of the ring buffer, all current items are newer than it.
    /// </summary>
    private void UpdateNewCount()
    {
        var count = ItemCount();
        if (_pauseTailKey is null)
        {
            SetNewCount(0);
            return;
        }

        for (var i = count - 1; i >= 0; i--)
        {
            if (string.Equals(KeyOf(ItemAt(i)), _pauseTailKey, StringComparison.Ordinal))
            {
                SetNewCount(count - 1 - i);
                return;
            }
        }
        SetNewCount(count);
    }

    // ---- position anchoring across per-tick rebuilds --------------------------------------------

    private void RememberAnchor(ScrollViewer sv)
    {
        // Only a content-scrolling (virtualizing) list has item-indexed offsets; for a pixel-scrolling
        // one we fall back to the raw offset, which is all that is meaningful there.
        if (!sv.CanContentScroll)
        {
            _anchorIndex = -1;
            _anchorKey = null;
            return;
        }

        _anchorIndex = (int)sv.VerticalOffset;
        _anchorKey = KeyOf(ItemAt(_anchorIndex));
    }

    private void RestoreAnchor(ScrollViewer sv)
    {
        if (IsSelectionActive())
            return;   // requirement 5

        if (_anchorKey is not null && sv.CanContentScroll)
        {
            var index = IndexOfKey(_anchorKey, _anchorIndex);
            if (index >= 0)
            {
                sv.ScrollToVerticalOffset(index);
                return;
            }
        }

        sv.ScrollToVerticalOffset(_savedOffset);
    }

    /// <summary>Find the anchor again after a rebuild, starting at its previous index (usually a hit).</summary>
    private int IndexOfKey(string key, int preferredIndex)
    {
        var count = ItemCount();
        if (count == 0)
            return -1;

        if (preferredIndex >= 0 && preferredIndex < count &&
            string.Equals(KeyOf(ItemAt(preferredIndex)), key, StringComparison.Ordinal))
            return preferredIndex;

        for (var i = count - 1; i >= 0; i--)
            if (string.Equals(KeyOf(ItemAt(i)), key, StringComparison.Ordinal))
                return i;

        return -1;
    }

    // ---- helpers --------------------------------------------------------------------------------

    private int ItemCount() => _list?.Items.Count ?? 0;

    private object? ItemAt(int index) =>
        _list is not null && index >= 0 && index < _list.Items.Count ? _list.Items[index] : null;

    /// <summary>
    /// Stable key of an item. Both consumers recreate their item objects on every tick, so reference
    /// identity cannot be used; the rendered text is the stable identity.
    /// </summary>
    private static string? KeyOf(object? item) => item?.ToString();

    /// <summary>
    /// True while the user is dragging (mouse down) or a wrapped text control holds a selection —
    /// in either case the view must not move (requirement 5).
    /// </summary>
    private bool IsSelectionActive()
    {
        if (_mouseDown)
            return true;

        return FindDescendant<TextBox>(this) is { } box && box.SelectionLength > 0;
    }

    private static bool IsDescendantOf(DependencyObject node, DependencyObject ancestor)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor))
                return true;
        return false;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is { } deeper)
                return deeper;
        }
        return null;
    }
}
