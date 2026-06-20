using System.Collections.ObjectModel;
using HarryDataServer.Services;

namespace HarryDataServer.ViewModels;

/// <summary>MSA tab root: one sub-view per module (M10, M11, M20, M21, M50).</summary>
public sealed class MsaViewModel
{
    private static readonly string[] ModuleKeys = { "M10", "M11", "M20", "M21", "M50" };

    public MsaViewModel(IMsaService msa)
    {
        Modules = new ObservableCollection<MsaModuleViewModel>(
            ModuleKeys.Select(m => new MsaModuleViewModel(msa, m)));
    }

    public ObservableCollection<MsaModuleViewModel> Modules { get; }

    /// <summary>Load the stored runs for every module/type (called once the DB is ready).</summary>
    public async Task LoadAllAsync()
    {
        foreach (var module in Modules)
            await module.LoadAsync().ConfigureAwait(true);
    }
}
