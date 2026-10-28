using System.Text.Json.Serialization;

namespace PumpTelemetry.Core;

public enum TelemetryAlertType
{
    HighPressure,
    OutOfOrderSequence,
    DeviceSilent
}

public sealed record TelemetryAlert(
    [property: JsonRequired] TelemetryAlertType Type,
    [property: JsonRequired] string DeviceId,
    [property: JsonRequired] DateTimeOffset Timestamp,
    [property: JsonRequired] string Message,
    TelemetryReading? Reading = null,
    double? Threshold = null);
