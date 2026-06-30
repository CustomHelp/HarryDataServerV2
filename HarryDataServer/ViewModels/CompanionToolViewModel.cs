using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using HarryDataServer.Services;

namespace HarryDataServer.ViewModels;

/// <summary>
/// One launchable companion tool on the Tools tab. The executable is discovered next to the
/// running DataServer exe (no hardcoded absolute paths); when found the button launches it,
/// otherwise it is disabled with a "not found" hint.
/// </summary>
public sealed class CompanionToolViewModel
{
    /// <summary>Companion app exe names (CLAUDE.md §16), in display order.</summary>
    private static readonly string[] ToolNames =
    {
        "HarryAnalysis", "HarryGraph", "HarryCounter", "HarryLimitSample", "HarryCollageCreator",
    };

    private readonly ILogService _log;

    private CompanionToolViewModel(string name, string? exePath, ILogService log)
    {
        _log = log;
        Name = name;
        ExePath = exePath;
        LaunchCommand = new RelayCommand(Launch, () => IsAvailable);
    }

    public string Name { get; }
    public string? ExePath { get; }
    public bool IsAvailable => ExePath is not null;

    /// <summary>Hint shown next to the button: the resolved path, or why it is disabled.</summary>
    public string Hint => ExePath ?? "not found next to exe";

    public IRelayCommand LaunchCommand { get; }

    /// <summary>Build the tool list, resolving each exe next to the running executable.</summary>
    public static IReadOnlyList<CompanionToolViewModel> Discover(ILogService log)
    {
        var baseDir = AppContext.BaseDirectory;
        return ToolNames.Select(n => new CompanionToolViewModel(n, FindExe(baseDir, n), log)).ToList();
    }

    /// <summary>Look for &lt;name&gt;.exe next to the exe, then in a same-named sibling subfolder.</summary>
    private static string? FindExe(string baseDir, string name)
    {
        string[] candidates =
        {
            Path.Combine(baseDir, name + ".exe"),
            Path.Combine(baseDir, name, name + ".exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private void Launch()
    {
        if (ExePath is null)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(ExePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory,
            });
            _log.Information("Launched companion tool: {Tool}.", Name);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to launch companion tool {Tool} ({Path}).", Name, ExePath);
        }
    }
}
