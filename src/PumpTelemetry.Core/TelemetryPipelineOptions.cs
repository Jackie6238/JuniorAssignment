namespace PumpTelemetry.Core;

public sealed class TelemetryPipelineOptions
{
    public double PressureThreshold { get; init; } = 120;

    public TimeSpan SilentTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan SilentCheckInterval { get; init; } = TimeSpan.FromSeconds(1);

    public int QueueCapacity { get; init; } = 1024;
}
