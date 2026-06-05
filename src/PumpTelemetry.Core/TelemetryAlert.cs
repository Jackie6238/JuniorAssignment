namespace PumpTelemetry.Core;

public enum TelemetryAlertType
{
    HighPressure,
    OutOfOrderSequence,
    DeviceSilent
}

public sealed record TelemetryAlert(
    TelemetryAlertType Type,
    string DeviceId,
    DateTimeOffset Timestamp,
    string Message,
    TelemetryReading? Reading = null);
