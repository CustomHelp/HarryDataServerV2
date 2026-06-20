using System.IO;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace HarryDataServer.Services;

/// <summary>
/// Serilog-backed implementation of <see cref="ILogService"/>. Writes a daily
/// rolling log file to the path configured in Harry.ini ([General] LogFilePath)
/// and mirrors output to the console for diagnostics.
/// </summary>
public sealed class SerilogService : ILogService
{
    private readonly Logger _logger;

    public SerilogService(string logFilePath, bool loggingActive, ILogEventSink? uiSink = null)
    {
        // Ensure the log directory exists before Serilog opens the file sink.
        if (!string.IsNullOrWhiteSpace(logFilePath))
        {
            try
            {
                Directory.CreateDirectory(logFilePath);
            }
            catch
            {
                // Fall back to a local Logs folder if the configured path is unavailable.
                logFilePath = Path.Combine(AppContext.BaseDirectory, "Logs");
                Directory.CreateDirectory(logFilePath);
            }
        }
        else
        {
            logFilePath = Path.Combine(AppContext.BaseDirectory, "Logs");
            Directory.CreateDirectory(logFilePath);
        }

        var minLevel = loggingActive ? LogEventLevel.Debug : LogEventLevel.Warning;
        var filePattern = Path.Combine(logFilePath, "HarryDataServer-.log");

        const string template =
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

        var config = new LoggerConfiguration()
            .MinimumLevel.Is(minLevel)
            .WriteTo.File(
                filePattern,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 90,
                shared: true,
                outputTemplate: template)
            .WriteTo.Console(outputTemplate: template);

        // Mirror into the in-memory buffer that backs the UI Log tab.
        if (uiSink is not null)
            config = config.WriteTo.Sink(uiSink);

        _logger = config.CreateLogger();

        _logger.Information("Logging initialized. Path={LogFilePath} Active={Active}", logFilePath, loggingActive);
    }

    public void Debug(string message, params object?[] propertyValues) => _logger.Debug(message, propertyValues);

    public void Information(string message, params object?[] propertyValues) => _logger.Information(message, propertyValues);

    public void Warning(string message, params object?[] propertyValues) => _logger.Warning(message, propertyValues);

    public void Error(string message, params object?[] propertyValues) => _logger.Error(message, propertyValues);

    public void Error(Exception exception, string message, params object?[] propertyValues) =>
        _logger.Error(exception, message, propertyValues);

    public void Shutdown()
    {
        _logger.Information("Logging shutting down.");
        _logger.Dispose();
    }
}
