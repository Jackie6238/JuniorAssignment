namespace PumpTelemetry.Core;

public sealed record TelemetryReading(
    string DeviceId,
    DateTimeOffset Timestamp,
    double Pressure,
    double Temperature,
    long SequenceNumber);
