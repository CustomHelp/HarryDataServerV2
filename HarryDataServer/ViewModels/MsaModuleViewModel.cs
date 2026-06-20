using HarryDataServer.Models;
using HarryDataServer.Services;

namespace HarryDataServer.ViewModels;

/// <summary>One module's MSA view (M10..M50): three run views (MSA1, MSA3, LimitSample).</summary>
public sealed class MsaModuleViewModel
{
    public MsaModuleViewModel(IMsaService msa, string module)
    {
        Module = module;
        Msa1 = new MsaRunsViewModel(msa, module, MsaType.Msa1);
        Msa3 = new MsaRunsViewModel(msa, module, MsaType.Msa3);
        LimitSample = new MsaRunsViewModel(msa, module, MsaType.LimitSample);
    }

    public string Module { get; }
    public MsaRunsViewModel Msa1 { get; }
    public MsaRunsViewModel Msa3 { get; }
    public MsaRunsViewModel LimitSample { get; }

    public async Task LoadAsync()
    {
        await Msa1.LoadAsync().ConfigureAwait(true);
        await Msa3.LoadAsync().ConfigureAwait(true);
        await LimitSample.LoadAsync().ConfigureAwait(true);
    }
}
