using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HarryCounter;

/// <summary>
/// One node in the HarryCounter error tree (port of RazorErrorCount's ErrorTreeNode).
/// Each node carries a headline NG count; the deepest level is a result breakdown with
/// "OK – n" / "NG – n" leaves. Title format: "&lt;Key&gt; – &lt;Count&gt;".
/// </summary>
public partial class ErrorTreeNode : ObservableObject
{
    private static readonly Brush Default = Frozen(0xE5, 0xE7, 0xEB);
    private static readonly Brush Ng = Frozen(0xEF, 0x44, 0x44);
    private static readonly Brush Ok = Frozen(0x22, 0xC5, 0x5E);

    public ErrorTreeNode(string key, int count, NodeKind kind = NodeKind.Group, bool expanded = false)
    {
        Key = key;
        Count = count;
        Title = $"{key} – {count}";
        Foreground = kind switch { NodeKind.Ng => Ng, NodeKind.Ok => Ok, _ => Default };
        _isExpanded = expanded;
    }

    public string Key { get; }
    public int Count { get; }
    public string Title { get; }
    public Brush Foreground { get; }
    public ObservableCollection<ErrorTreeNode> Children { get; } = new();

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public enum NodeKind { Group, Ok, Ng }
