using System.Collections.Concurrent;
using System.Threading.Channels;

namespace PumpTelemetry.Core;

public sealed class TelemetryPipeline : IAsyncDisposable
{
    private readonly Channel<TelemetryReading> _channel;
    private readonly TelemetryPipelineOptions _options;
    private readonly Func<TelemetryAlert, CancellationToken, ValueTask> _alertSink;
    private readonly ConcurrentDictionary<string, DeviceState> _deviceStates = new();
    private readonly CancellationTokenSource _monitorCts = new();
    private readonly List<Task> _runningTasks = new();
    private int _started;
    private int _stopped;
    private CancellationTokenRegistration _stopRegistration;

    public TelemetryPipeline(
        TelemetryPipelineOptions options,
        Func<TelemetryAlert, CancellationToken, ValueTask> alertSink)
    {
        _options = options;
        _alertSink = alertSink;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.QueueCapacity);

        _channel = Channel.CreateBounded<TelemetryReading>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public Task SubmitAsync(TelemetryReading reading, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(reading, cancellationToken).AsTask();
    }

    public bool TrySubmit(TelemetryReading reading)
    {
        return _channel.Writer.TryWrite(reading);
    }

    public void Start(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _stopped) == 1)
        {
            throw new InvalidOperationException("Pipeline has already been stopped.");
        }

        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException("Pipeline has already been started.");
        }

        if (cancellationToken.CanBeCanceled)
        {
            _stopRegistration = cancellationToken.Register(static state =>
            {
                ((TelemetryPipeline)state!).RequestStop();
            }, this);
        }

        _runningTasks.Add(Task.Run(ProcessReadingsAsync, CancellationToken.None));
        _runningTasks.Add(Task.Run(() => MonitorSilentDevicesAsync(_monitorCts.Token), CancellationToken.None));
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            RequestStop();
            _stopRegistration.Dispose();
        }

        await Task.WhenAll(_runningTasks).ConfigureAwait(false);
    }

    private void RequestStop()
    {
        _channel.Writer.TryComplete();
        _monitorCts.Cancel();
    }

    private async Task ProcessReadingsAsync()
    {
        try
        {
            await foreach (var reading in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await ProcessReadingAsync(reading).ConfigureAwait(false);
            }
        }
        catch
        {
            // A failed sink must release producers blocked on the bounded channel.
            RequestStop();
            throw;
        }
    }

    internal async ValueTask ProcessReadingAsync(TelemetryReading reading, CancellationToken cancellationToken = default)
    {
        var state = _deviceStates.GetOrAdd(reading.DeviceId, _ => new DeviceState());
        long? previousSequence = null;

        lock (state)
        {
            if (state.HasReading)
            {
                previousSequence = state.LastSequenceNumber;
            }

            state.HasReading = true;
            state.LastSeen = reading.Timestamp;
            state.SilentAlerted = false;

            if (!previousSequence.HasValue || reading.SequenceNumber > previousSequence.Value)
            {
                state.LastSequenceNumber = reading.SequenceNumber;
            }
        }

        if (reading.Pressure > _options.PressureThreshold)
        {
            await EmitAlertAsync(
                TelemetryAlertType.HighPressure,
                reading.DeviceId,
                reading.Timestamp,
                $"Pressure {reading.Pressure:F1} exceeded threshold {_options.PressureThreshold:F1}.",
                reading,
                cancellationToken,
                _options.PressureThreshold).ConfigureAwait(false);
        }

        if (previousSequence.HasValue && reading.SequenceNumber <= previousSequence.Value)
        {
            await EmitAlertAsync(
                TelemetryAlertType.OutOfOrderSequence,
                reading.DeviceId,
                reading.Timestamp,
                $"Sequence {reading.SequenceNumber} arrived after {previousSequence.Value}.",
                reading,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask CheckSilentDevicesAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        foreach (var (deviceId, state) in _deviceStates)
        {
            DateTimeOffset lastSeen;

            lock (state)
            {
                if (!state.HasReading || state.SilentAlerted || now - state.LastSeen <= _options.SilentTimeout)
                {
                    continue;
                }

                lastSeen = state.LastSeen;
                state.SilentAlerted = true;
            }

            await EmitAlertAsync(
                TelemetryAlertType.DeviceSilent,
                deviceId,
                now,
                $"Device has been silent since {lastSeen:O}.",
                null,
                cancellationToken,
                _options.SilentTimeout.TotalSeconds).ConfigureAwait(false);
        }
    }

    private async Task MonitorSilentDevicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_options.SilentCheckInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await CheckSilentDevicesAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            RequestStop();
            throw;
        }
    }

    private ValueTask EmitAlertAsync(
        TelemetryAlertType type,
        string deviceId,
        DateTimeOffset timestamp,
        string message,
        TelemetryReading? reading,
        CancellationToken cancellationToken,
        double? threshold = null)
    {
        return _alertSink(new TelemetryAlert(type, deviceId, timestamp, message, reading, threshold), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _monitorCts.Dispose();
        }
    }

    private sealed class DeviceState
    {
        public bool HasReading { get; set; }

        public DateTimeOffset LastSeen { get; set; }

        public long LastSequenceNumber { get; set; }

        public bool SilentAlerted { get; set; }
    }
}
