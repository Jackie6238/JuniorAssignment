using System.Collections.Concurrent;
using PumpTelemetry.Core;
using Xunit;

namespace PumpTelemetry.Tests;

public sealed class TelemetryPipelineTests
{
    [Fact]
    public async Task ProcessReadingAsync_WhenPressureExceedsThreshold_EmitsHighPressureAlert()
    {
        var alerts = new List<TelemetryAlert>();
        var pipeline = CreatePipeline(alerts, new TelemetryPipelineOptions { PressureThreshold = 100 });
        var reading = Reading(pressure: 125);

        await pipeline.ProcessReadingAsync(reading);

        var alert = Assert.Single(alerts);
        Assert.Equal(TelemetryAlertType.HighPressure, alert.Type);
        Assert.Equal(reading.DeviceId, alert.DeviceId);
    }

    [Fact]
    public async Task ProcessReadingAsync_WhenSequenceMovesBackward_EmitsOutOfOrderAlert()
    {
        var alerts = new List<TelemetryAlert>();
        var pipeline = CreatePipeline(alerts);

        await pipeline.ProcessReadingAsync(Reading(sequenceNumber: 10));
        await pipeline.ProcessReadingAsync(Reading(sequenceNumber: 9));

        var alert = Assert.Single(alerts);
        Assert.Equal(TelemetryAlertType.OutOfOrderSequence, alert.Type);
        Assert.Contains("after 10", alert.Message);
    }

    [Fact]
    public async Task CheckSilentDevicesAsync_WhenDeviceExceedsSilentTimeout_EmitsSilentAlertOnce()
    {
        var alerts = new List<TelemetryAlert>();
        var pipeline = CreatePipeline(alerts, new TelemetryPipelineOptions
        {
            SilentTimeout = TimeSpan.FromMilliseconds(100)
        });

        var timestamp = DateTimeOffset.UtcNow;
        await pipeline.ProcessReadingAsync(Reading(timestamp: timestamp));

        await pipeline.CheckSilentDevicesAsync(timestamp.AddMilliseconds(150));
        await pipeline.CheckSilentDevicesAsync(timestamp.AddMilliseconds(250));

        var alert = Assert.Single(alerts);
        Assert.Equal(TelemetryAlertType.DeviceSilent, alert.Type);
    }

    [Fact]
    public async Task Pipeline_WhenReadingsAreSubmittedConcurrently_ProcessesAllWithoutBlockingProducers()
    {
        var alerts = new ConcurrentBag<TelemetryAlert>();
        await using var pipeline = new TelemetryPipeline(
            new TelemetryPipelineOptions { PressureThreshold = 100 },
            (alert, _) =>
            {
                alerts.Add(alert);
                return ValueTask.CompletedTask;
            });

        pipeline.Start();

        var submissions = Enumerable.Range(1, 100)
            .Select(sequenceNumber => Task.Run(() => pipeline.SubmitAsync(
                Reading(
                    deviceId: $"pump-{sequenceNumber:000}",
                    pressure: 125,
                    sequenceNumber: sequenceNumber))));

        await Task.WhenAll(submissions);
        await pipeline.StopAsync();

        Assert.Equal(100, alerts.Count);
        Assert.All(alerts, alert => Assert.Equal(TelemetryAlertType.HighPressure, alert.Type));
    }

    [Fact]
    public async Task Pipeline_WhenReadingsAreSubmittedOutOfOrder_ProcessesInChannelArrivalOrder()
    {
        var alerts = new List<TelemetryAlert>();
        await using var pipeline = CreatePipeline(alerts);

        pipeline.Start();

        await pipeline.SubmitAsync(Reading(sequenceNumber: 10));
        await pipeline.SubmitAsync(Reading(sequenceNumber: 9));
        await pipeline.StopAsync();

        var alert = Assert.Single(alerts);
        Assert.Equal(TelemetryAlertType.OutOfOrderSequence, alert.Type);
        Assert.Contains("after 10", alert.Message);
    }

    [Fact]
    public async Task StopAsync_WhenCalledMultipleTimes_CompletesSuccessfully()
    {
        var alerts = new List<TelemetryAlert>();
        await using var pipeline = CreatePipeline(alerts);

        pipeline.Start();

        await pipeline.StopAsync();
        await pipeline.StopAsync();
    }

    [Fact]
    public async Task FailedReadingSinkUnblocksProducers_AndReportsFailureOnStop()
    {
        var pipeline = new TelemetryPipeline(new TelemetryPipelineOptions { QueueCapacity = 1 },
            (_, _) => ValueTask.FromException(new IOException("disk full")));
        pipeline.Start();
        var producer = Task.Run(async () =>
        {
            for (var i = 1; i <= 100; i++)
            {
                await pipeline.SubmitAsync(Reading(pressure: 125, sequenceNumber: i));
            }
        });
        await Assert.ThrowsAsync<System.Threading.Channels.ChannelClosedException>(
            () => producer.WaitAsync(TimeSpan.FromSeconds(5)));
        var failure = await Assert.ThrowsAsync<IOException>(() => pipeline.StopAsync());
        Assert.Equal("disk full", failure.Message);
        await Assert.ThrowsAsync<IOException>(async () => await pipeline.DisposeAsync());
    }

    [Fact]
    public async Task FailedSilenceSinkStopsPipeline_AndReportsFailureOnStop()
    {
        var reachedSink = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new TelemetryPipeline(new TelemetryPipelineOptions
        {
            SilentTimeout = TimeSpan.FromMilliseconds(1),
            SilentCheckInterval = TimeSpan.FromMilliseconds(10)
        }, (_, _) =>
        {
            reachedSink.SetResult();
            return ValueTask.FromException(new IOException("cannot save silence"));
        });
        await pipeline.ProcessReadingAsync(Reading(timestamp: DateTimeOffset.UtcNow.AddSeconds(-1)));
        pipeline.Start();
        await reachedSink.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var failure = await Assert.ThrowsAsync<IOException>(() => pipeline.StopAsync());
        Assert.Equal("cannot save silence", failure.Message);
        Assert.False(pipeline.TrySubmit(Reading()));
        await Assert.ThrowsAsync<IOException>(async () => await pipeline.DisposeAsync());
    }

    [Fact]
    public async Task PressureAtThresholdDoesNotAlert_AndSilenceResetsAfterNewReading()
    {
        var alerts = new List<TelemetryAlert>();
        await using var pipeline = CreatePipeline(alerts);
        var now = DateTimeOffset.UtcNow;
        await pipeline.ProcessReadingAsync(Reading(timestamp: now, pressure: 120));
        await pipeline.CheckSilentDevicesAsync(now.AddSeconds(5));
        Assert.Empty(alerts);
        await pipeline.CheckSilentDevicesAsync(now.AddSeconds(6));
        await pipeline.CheckSilentDevicesAsync(now.AddSeconds(7));
        Assert.Single(alerts);
        await pipeline.ProcessReadingAsync(Reading(timestamp: now.AddSeconds(8), sequenceNumber: 2));
        await pipeline.CheckSilentDevicesAsync(now.AddSeconds(14));
        Assert.Equal(2, alerts.Count);
        Assert.All(alerts, a => Assert.Equal(TelemetryAlertType.DeviceSilent, a.Type));
    }

    [Fact]
    public async Task DuplicateAndBackwardSequencesDoNotLowerSequenceHighWaterMark()
    {
        var alerts = new List<TelemetryAlert>();
        await using var pipeline = CreatePipeline(alerts);
        foreach (var sequence in new long[] { 10, 10, 8, 9, 11 })
        {
            await pipeline.ProcessReadingAsync(Reading(sequenceNumber: sequence));
        }
        Assert.Equal(3, alerts.Count);
        Assert.All(alerts, a => Assert.Contains("after 10", a.Message));
    }

    private static TelemetryPipeline CreatePipeline(
        ICollection<TelemetryAlert> alerts,
        TelemetryPipelineOptions? options = null)
    {
        return new TelemetryPipeline(options ?? new TelemetryPipelineOptions(), (alert, _) =>
        {
            alerts.Add(alert);
            return ValueTask.CompletedTask;
        });
    }

    private static TelemetryReading Reading(
        string deviceId = "pump-01",
        DateTimeOffset? timestamp = null,
        double pressure = 90,
        double temperature = 70,
        long sequenceNumber = 1)
    {
        return new TelemetryReading(
            deviceId,
            timestamp ?? DateTimeOffset.UtcNow,
            pressure,
            temperature,
            sequenceNumber);
    }
}
