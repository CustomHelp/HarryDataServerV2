using HarryDataServer.Models;

namespace HarryDataServer.Communication;

/// <summary>Raised when a "Results" telegram has been parsed into measurement samples.</summary>
public sealed class ResultsTelegramEventArgs : EventArgs
{
    public ResultsTelegramEventArgs(ParsedTelegram telegram, IReadOnlyList<MeasurementSample> measurements)
    {
        Telegram = telegram;
        Measurements = measurements;
    }

    public ParsedTelegram Telegram { get; }
    public IReadOnlyList<MeasurementSample> Measurements { get; }
}

/// <summary>Raised when a "Settings" telegram has been parsed into limit samples.</summary>
public sealed class SettingsTelegramEventArgs : EventArgs
{
    public SettingsTelegramEventArgs(ParsedTelegram telegram, IReadOnlyList<SettingSample> settings)
    {
        Telegram = telegram;
        Settings = settings;
    }

    public ParsedTelegram Telegram { get; }
    public IReadOnlyList<SettingSample> Settings { get; }
}

/// <summary>Raised when a "Diagnostic" telegram is received (written to CSV only, Phase 7).</summary>
public sealed class DiagnosticTelegramEventArgs : EventArgs
{
    public DiagnosticTelegramEventArgs(ParsedTelegram telegram) => Telegram = telegram;

    public ParsedTelegram Telegram { get; }
}
