namespace HarryDataServer.Services;

/// <summary>
/// Application-wide structured logging abstraction. Backed by Serilog
/// (see <see cref="SerilogService"/>). All log messages are in English.
/// </summary>
public interface ILogService
{
    void Debug(string message, params object?[] propertyValues);
    void Information(string message, params object?[] propertyValues);
    void Warning(string message, params object?[] propertyValues);
    void Error(string message, params object?[] propertyValues);
    void Error(Exception exception, string message, params object?[] propertyValues);

    /// <summary>Flush and dispose the underlying sinks. Call once on shutdown.</summary>
    void Shutdown();
}
