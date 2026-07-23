using CommunityToolkit.Mvvm.ComponentModel;
using HarryDataServer.Models;
using HarryDataServer.Services;

namespace HarryDataServer.ViewModels;

/// <summary>One module's MSA view (M10..M50): three run views (MSA1, MSA3, LimitSample).</summary>
public sealed partial class MsaModuleViewModel : ObservableObject
{
    public MsaModuleViewModel(IMsaService msa, string module, IPdfReportService pdf)
    {
        Module = module;
        Msa1 = new MsaRunsViewModel(msa, module, MsaType.Msa1, pdf);
        Msa3 = new MsaRunsViewModel(msa, module, MsaType.Msa3, pdf);
        LimitSample = new MsaRunsViewModel(msa, module, MsaType.LimitSample, pdf);
    }

    public string Module { get; }
    public MsaRunsViewModel Msa1 { get; }
    public MsaRunsViewModel Msa3 { get; }
    public MsaRunsViewModel LimitSample { get; }

    /// <summary>Which type sub-tab is shown (0=MSA1, 1=MSA3, 2=LimitSample). Bound to the inner
    /// TabControl; switching reloads that type (task A1).</summary>
    private int _selectedTypeIndex;
    public int SelectedTypeIndex
    {
        get => _selectedTypeIndex;
        set { if (SetProperty(ref _selectedTypeIndex, value)) _ = ActiveType.LoadAsync(); }
    }

    /// <summary>The run view for the currently selected type sub-tab.</summary>
    public MsaRunsViewModel ActiveType => SelectedTypeIndex switch
    {
        1 => Msa3,
        2 => LimitSample,
        _ => Msa1,
    };

    /// <summary>Reload only the currently visible type (task A1 — module/tab switch).</summary>
    public Task LoadActiveAsync() => ActiveType.LoadAsync();

    public async Task LoadAsync()
    {
        await Msa1.LoadAsync().ConfigureAwait(true);
        await Msa3.LoadAsync().ConfigureAwait(true);
        await LimitSample.LoadAsync().ConfigureAwait(true);
    }
}
