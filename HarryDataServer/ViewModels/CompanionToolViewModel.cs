using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using HarryDataServer.Services;

namespace HarryDataServer.ViewModels;

/// <summary>
/// One launchable companion tool on the Tools tab. The executable is discovered relative to the
/// running DataServer exe (no hardcoded absolute paths): first next to it / in a same-named
/// sibling subfolder (deployed layout), then in the sibling project's build output
/// (&lt;solutionRoot&gt;\&lt;Tool&gt;\bin\&lt;Config&gt;\&lt;tfm&gt;\&lt;Tool&gt;.exe, the dev layout).
/// When found the button launches it, otherwise it is disabled with a "not found" hint.
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
    public string Hint => ExePath ?? "not found (next to exe or in solution build output)";

    public IRelayCommand LaunchCommand { get; }

    /// <summary>Build the tool list, resolving each exe next to the running executable.</summary>
    public static IReadOnlyList<CompanionToolViewModel> Discover(ILogService log)
    {
        var baseDir = AppContext.BaseDirectory;
        return ToolNames.Select(n => new CompanionToolViewModel(n, FindExe(baseDir, n), log)).ToList();
    }

    /// <summary>
    /// Resolve a tool exe. Deployed layout: next to the exe, or a same-named sibling subfolder.
    /// Dev layout: each tool builds to its own project folder
    /// (&lt;solutionRoot&gt;\&lt;Tool&gt;\bin\&lt;Config&gt;\&lt;tfm&gt;\&lt;Tool&gt;.exe), so reuse the server's
    /// own bin\&lt;Config&gt;\&lt;tfm&gt; tail with the tool's project name swapped in.
    /// </summary>
    private static string? FindExe(string baseDir, string name)
    {
        var candidates = new List<string>
        {
            Path.Combine(baseDir, name + ".exe"),          // deployed: all exes in one folder
            Path.Combine(baseDir, name, name + ".exe"),    // deployed: same-named sibling subfolder
        };

        // Dev layout: baseDir is ...\HarryDataServer\bin\<Config>\<tfm>\ — walk up to the solution
        // root and point at the sibling tool project's matching build output.
        var tfmDir = new DirectoryInfo(baseDir);            // net8.0-windows
        var configDir = tfmDir.Parent;                     // Debug / Release
        var binDir = configDir?.Parent;                    // bin
        var solutionRoot = binDir?.Parent?.Parent;         // <solutionRoot> (skip the server project dir)
        if (configDir is not null && solutionRoot is not null)
        {
            candidates.Add(Path.Combine(
                solutionRoot.FullName, name, "bin", configDir.Name, tfmDir.Name, name + ".exe"));
        }

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
