using System.IO;
using System.Windows;
using HarryDataServer.Communication;
using HarryDataServer.Configuration;
using HarryDataServer.Infrastructure;
using HarryDataServer.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HarryDataServer;

/// <summary>
/// Application entry point. Builds the dependency-injection container, wires up
/// the core services (configuration + logging for Phase 1) and shows the main
/// window. Additional services are registered here as later phases are added.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _services;
    private ILogService? _log;
    private readonly CancellationTokenSource _shutdownCts = new();

    /// <summary>Resolved service provider, available to controls/windows after startup.</summary>
    public IServiceProvider Services =>
        _services ?? throw new InvalidOperationException("Service provider is not initialized yet.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _services = BuildServiceProvider();
            _log = _services.GetRequiredService<ILogService>();

            var config = _services.GetRequiredService<IConfigService>();
            _log.Information("HarryDataServer starting. Config={IniPath} Cameras={Count}",
                config.IniPath, config.Config.Cameras.Count);

            var window = _services.GetRequiredService<MainWindow>();
            window.Show();

            // Kick off the database startup sequence in the background so the UI
            // stays responsive while MySQL connects / schema is provisioned.
            var database = _services.GetRequiredService<IDatabaseService>();
            _ = Task.Run(() => database.StartAsync(_shutdownCts.Token));

            // Start the camera clients in parallel (independent of the database).
            var cameras = _services.GetRequiredService<ICameraService>();
            _ = Task.Run(() => cameras.StartAsync(_shutdownCts.Token));

            // Start the measurement pipeline (waits internally for the database to be ready).
            var measurements = _services.GetRequiredService<IMeasurementProcessor>();
            _ = Task.Run(() => measurements.StartAsync(_shutdownCts.Token));

            // Start the SPS server (7 channels) — independent of cameras/database.
            var sps = _services.GetRequiredService<ISpsServer>();
            _ = Task.Run(() => sps.StartAsync(_shutdownCts.Token));

            // Start the Phase 6 consumers (each on its own background task).
            var settings = _services.GetRequiredService<ISettingsProcessor>();
            _ = Task.Run(() => settings.StartAsync(_shutdownCts.Token));

            var diagnostics = _services.GetRequiredService<IDiagnosticProcessor>();
            _ = Task.Run(() => diagnostics.StartAsync(_shutdownCts.Token));

            var csv = _services.GetRequiredService<ICsvService>();
            _ = Task.Run(() => csv.StartAsync(_shutdownCts.Token));

            var imageCleanup = _services.GetRequiredService<IImageCleanupService>();
            _ = Task.Run(() => imageCleanup.StartAsync(_shutdownCts.Token));

            var msa = _services.GetRequiredService<IMsaService>();
            _ = Task.Run(() => msa.StartAsync(_shutdownCts.Token));

            var collage = _services.GetRequiredService<ICollageService>();
            _ = Task.Run(() => collage.StartAsync(_shutdownCts.Token));

            // Part-exit orchestrator: registers the channel-2 handler (CSV/Collage/Images + ACK).
            var partExit = _services.GetRequiredService<IPartExitOrchestrator>();
            _ = Task.Run(() => partExit.StartAsync(_shutdownCts.Token));
        }
        catch (Exception ex)
        {
            // Surface fatal startup failures (e.g. missing Harry.ini) to the operator.
            _log?.Error(ex, "Fatal error during startup.");
            MessageBox.Show(
                $"HarryDataServer failed to start:\n\n{ex.Message}",
                "Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Information("HarryDataServer shutting down.");
        _shutdownCts.Cancel();

        // Best-effort final flush of all queue-backed processors before the process exits.
        try
        {
            var stopTasks = new[]
            {
                _services?.GetService<IMeasurementProcessor>()?.StopAsync(),
                _services?.GetService<ISettingsProcessor>()?.StopAsync(),
                _services?.GetService<IDiagnosticProcessor>()?.StopAsync(),
                _services?.GetService<IPartExitOrchestrator>()?.StopAsync(),
                _services?.GetService<ICsvService>()?.StopAsync(),
                _services?.GetService<IMsaService>()?.StopAsync(),
                _services?.GetService<IImageCleanupService>()?.StopAsync(),
                _services?.GetService<ICollageService>()?.StopAsync(),
            }.Where(t => t is not null).Cast<Task>().ToArray();

            Task.WhenAll(stopTasks).Wait(TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            _log?.Error(ex, "Error during processor shutdown.");
        }

        _log?.Shutdown();
        _services?.Dispose();
        _shutdownCts.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Compose the DI container. All long-lived services are singletons, matching
    /// the architecture defined in CLAUDE.md section 14.
    /// </summary>
    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        // --- Configuration (loaded once from Harry.ini) ---
        var iniPath = ResolveIniPath();
        services.AddSingleton<IConfigService>(_ => new IniConfigService(iniPath));

        // --- Logging (Serilog) + in-memory buffer for the UI Log tab ---
        services.AddSingleton<InMemoryLogSink>();
        services.AddSingleton<ILogBuffer>(sp => sp.GetRequiredService<InMemoryLogSink>());
        services.AddSingleton<ILogService>(sp =>
        {
            var general = sp.GetRequiredService<IConfigService>().Config.General;
            return new SerilogService(general.LogFilePath, general.LoggingActive,
                sp.GetRequiredService<InMemoryLogSink>());
        });

        // --- System health (central fault registry surfaced on the SPS KeepAlive channel) ---
        services.AddSingleton<ISystemHealth, SystemHealthService>();

        // --- Database (Phase 2): repository, partitions, JSON templates, orchestration ---
        services.AddSingleton<JsonTemplateLoader>();
        services.AddSingleton(sp =>
        {
            var mysql = sp.GetRequiredService<IConfigService>().Config.MySql;
            return new MySqlRepository(mysql, sp.GetRequiredService<ILogService>());
        });
        services.AddSingleton<PartitionManager>();
        services.AddSingleton<IDatabaseService, MySqlDatabaseService>();

        // --- Cameras (Phase 3): telegram parser + per-camera TCP clients ---
        services.AddSingleton<TelegramParser>();
        services.AddSingleton<ICameraService, CameraConnectionService>();

        // --- Measurement pipeline (Phase 4): definition cache + queue processor ---
        services.AddSingleton<MeasurementDefinitionCache>();
        services.AddSingleton<IMeasurementProcessor, MeasurementProcessor>();

        // --- SPS server (Phase 5): 7-channel PLC TCP server ---
        services.AddSingleton<ISpsServer, TcpSpsServer>();

        // --- Phase 6 consumers: settings, diagnostic ---
        services.AddSingleton<SettingDefinitionCache>();
        services.AddSingleton<ISettingsProcessor, SettingsProcessor>();
        services.AddSingleton<IDiagnosticProcessor, DiagnosticProcessor>();

        // --- CSV export (Phase 7): main per-part CSV ---
        services.AddSingleton<ICsvService, CsvExportService>();

        // --- Image cleanup (Phase 8) + MSA engine (Phase 10) ---
        services.AddSingleton<IImageCleanupService, ImageCleanupService>();
        services.AddSingleton<MsaReferenceLoader>();
        services.AddSingleton<IMsaService, MsaService>();

        // --- Collage generator (Phase 9) ---
        services.AddSingleton<CollageIniReader>();
        services.AddSingleton<CollageComposer>();
        services.AddSingleton<ICollageService, CollageService>();

        // --- Part-exit orchestrator + image handler (parallel sequence + ACK) ---
        services.AddSingleton<ImageHandler>();
        services.AddSingleton<IPartExitOrchestrator, PartExitOrchestrator>();

        // --- UI (Phase 11): main view model + window ---
        services.AddSingleton<ViewModels.MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    /// <summary>Central configuration folder (Harry.ini, Templates\, and later
    /// Collage.ini / MSA references). Overridable via the HARRY_CONFIG_DIR env var.</summary>
    public const string DefaultConfigDir = @"F:\002_Configs";

    /// <summary>
    /// Locate Harry.ini in priority order: HARRY_CONFIG_DIR env var, the central
    /// config folder, next to the executable, then the legacy deployment path.
    /// </summary>
    private static string ResolveIniPath()
    {
        var candidates = new List<string>();

        var envDir = Environment.GetEnvironmentVariable("HARRY_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(envDir))
            candidates.Add(Path.Combine(envDir, "Harry.ini"));

        candidates.Add(Path.Combine(DefaultConfigDir, "Harry.ini"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "Harry.ini"));
        candidates.Add(@"D:\HarryDataServer\Harry.ini");

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // Fall back to the highest-priority location so the error message points there.
        return candidates[0];
    }
}
