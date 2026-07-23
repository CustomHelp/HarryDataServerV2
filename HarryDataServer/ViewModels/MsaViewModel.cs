using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HarryDataServer.Services;

namespace HarryDataServer.ViewModels;

/// <summary>MSA tab root: one sub-view per module (M10, M11, M20, M21, M50).</summary>
public sealed partial class MsaViewModel : ObservableObject
{
    private static readonly string[] ModuleKeys = { "M10", "M11", "M20", "M21", "M50" };

    public MsaViewModel(IMsaService msa, IPdfReportService pdf)
    {
        Modules = new ObservableCollection<MsaModuleViewModel>(
            ModuleKeys.Select(m => new MsaModuleViewModel(msa, m, pdf)));
    }

    public ObservableCollection<MsaModuleViewModel> Modules { get; }

    /// <summary>The module sub-tab currently shown (bound to the outer TabControl). Switching modules
    /// reloads that module's active type (task A1).</summary>
    private MsaModuleViewModel? _selectedModule;
    public MsaModuleViewModel? SelectedModule
    {
        get => _selectedModule;
        set { if (SetProperty(ref _selectedModule, value)) _ = value?.LoadActiveAsync(); }
    }

    /// <summary>Reload the currently visible module/type — called when the MSA tab becomes visible (task A1).</summary>
    public Task ReloadActiveAsync() => SelectedModule?.LoadActiveAsync() ?? Task.CompletedTask;

    /// <summary>Load the stored runs for every module/type (called once the DB is ready).</summary>
    public async Task LoadAllAsync()
    {
        foreach (var module in Modules)
            await module.LoadAsync().ConfigureAwait(true);
    }
}
