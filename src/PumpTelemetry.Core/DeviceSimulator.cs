namespace PumpTelemetry.Core;

public sealed class DeviceSimulator
{
    private const double AlarmChance = 0.10;
    private const double OutOfOrderShareOfAlarms = 0.20;
    private const double SilentPeriodChance = 0.02;

    private readonly string _deviceId;
    private readonly Random _random;
    private long _sequenceNumber;

    public DeviceSimulator(string deviceId, int? seed = null)
    {
        _deviceId = deviceId;
        _random = seed.HasValue ? new Random(seed.Value) : Random.Shared;
    }

    public async Task RunAsync(
        Func<TelemetryReading, CancellationToken, ValueTask> publish,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_random.Next(100, 501), cancellationToken).ConfigureAwait(false);

            if (_random.NextDouble() < SilentPeriodChance)
            {
                await Task.Delay(TimeSpan.FromSeconds(_random.Next(6, 10)), cancellationToken).ConfigureAwait(false);
            }

            var nextSequence = Interlocked.Increment(ref _sequenceNumber);
            var shouldAlarm = _random.NextDouble() < AlarmChance;
            var shouldEmitOutOfOrder = shouldAlarm
                && nextSequence > 2
                && _random.NextDouble() < OutOfOrderShareOfAlarms;

            if (shouldEmitOutOfOrder)
            {
                nextSequence -= 2;
            }

            var pressure = shouldAlarm && !shouldEmitOutOfOrder
                ? 122 + _random.NextDouble() * 13
                : 80 + _random.NextDouble() * 35;

            var reading = new TelemetryReading(
                _deviceId,
                DateTimeOffset.UtcNow,
                Pressure: pressure,
                Temperature: 55 + _random.NextDouble() * 35,
                SequenceNumber: nextSequence);

            await publish(reading, cancellationToken).ConfigureAwait(false);
        }
    }
}
