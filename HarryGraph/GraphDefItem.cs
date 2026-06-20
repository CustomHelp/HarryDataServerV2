using CommunityToolkit.Mvvm.ComponentModel;
using HarryShared.Data;

namespace HarryGraph;

/// <summary>A measurement definition with a per-panel selection flag (checkable list).</summary>
public partial class GraphDefItem : ObservableObject
{
    private readonly Action _onChanged;

    public GraphDefItem(MeasurementDefinitionRow def, Action onChanged)
    {
        Definition = def;
        _onChanged = onChanged;
    }

    public MeasurementDefinitionRow Definition { get; }
    public string Label => Definition.Label;

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _onChanged();
}
