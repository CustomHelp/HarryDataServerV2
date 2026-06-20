using CommunityToolkit.Mvvm.ComponentModel;
using HarryShared.Data;

namespace HarryGraph;

/// <summary>A measurement definition wrapped with a selection flag for the pick list.</summary>
public partial class DefItem : ObservableObject
{
    public DefItem(MeasurementDefinitionRow def, Action onSelectionChanged)
    {
        Definition = def;
        _onSelectionChanged = onSelectionChanged;
    }

    private readonly Action _onSelectionChanged;

    public MeasurementDefinitionRow Definition { get; }
    public string Label => Definition.Label;
    public int Id => Definition.Id;

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _onSelectionChanged();
}
