using System.Globalization;
using PumpTelemetry.Core;

namespace PumpTelemetry.Monitor;

public static class HistoryDisplay
{
    public static void Write(TextWriter output, IReadOnlyList<TelemetryAlert> alerts)
    {
        output.WriteLine($"Alert history ({alerts.Count}, newest first):");
        if (alerts.Count == 0)
        {
            output.WriteLine("No saved alerts.");
            return;
        }

        foreach (var alert in alerts)
        {
            var detail = alert.Type switch
            {
                TelemetryAlertType.HighPressure when alert.Reading is not null =>
                    $"pressure={alert.Reading.Pressure.ToString("F1", CultureInfo.InvariantCulture)}",
                TelemetryAlertType.OutOfOrderSequence when alert.Reading is not null =>
                    $"sequence={alert.Reading.SequenceNumber}",
                _ => ""
            };
            if (alert.Threshold.HasValue)
            {
                var unit = alert.Type == TelemetryAlertType.DeviceSilent ? "s" : "";
                detail += $" threshold={alert.Threshold.Value.ToString("G", CultureInfo.InvariantCulture)}{unit}";
            }

            output.WriteLine($"[{alert.Timestamp:O}] {alert.Type} {alert.DeviceId}: {detail.Trim()} | {alert.Message}");
        }
    }
}
