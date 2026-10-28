using System.Text.Json;
using PumpTelemetry.Core;
using Xunit;

namespace PumpTelemetry.Tests;

public sealed class JsonAlertHistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pump-history-tests-" + Guid.NewGuid());
    private string HistoryPath => Path.Combine(_directory, "nested", "history.json");
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-01-02T03:04:05Z");

    [Fact]
    public async Task MissingFileStartsEmpty_AndAllAlertTypesRoundTripAcrossRestart()
    {
        var expected = new[]
        {
            Alert("pump-pressure"),
            new TelemetryAlert(TelemetryAlertType.OutOfOrderSequence, "pump-sequence", Time.AddSeconds(1),
                "Sequence 9 arrived after 10.", new TelemetryReading("pump-sequence", Time, 90, 70, 9)),
            new TelemetryAlert(TelemetryAlertType.DeviceSilent, "pump-silent", Time.AddSeconds(2),
                "Device silent.", Threshold: 5)
        };
        await using (var history = new JsonAlertHistory(HistoryPath))
        {
            Assert.Empty(await history.GetRecentAsync());
            foreach (var alert in expected)
            {
                await history.AppendAsync(alert);
            }
        }

        var json = await File.ReadAllTextAsync(HistoryPath);
        Assert.Contains("\"HighPressure\"", json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(3, document.RootElement.GetArrayLength());
        await using var reopened = new JsonAlertHistory(HistoryPath);
        Assert.Equal(expected.Reverse(), await reopened.GetRecentAsync());
        await reopened.AppendAsync(Alert("new-after-restart", Time.AddSeconds(3)));
        Assert.Equal(4, (await reopened.GetRecentAsync()).Count);
    }

    [Fact]
    public async Task DefaultLimitShowsLatestTen_WithoutRemovingOlderRecords()
    {
        await using var history = new JsonAlertHistory(HistoryPath);
        for (var i = 0; i < 15; i++)
        {
            await history.AppendAsync(Alert($"pump-{i}", Time.AddSeconds(i)));
        }
        var recent = await history.GetRecentAsync();
        Assert.Equal(10, recent.Count);
        Assert.Equal("pump-14", recent[0].DeviceId);
        Assert.Equal("pump-5", recent[^1].DeviceId);
        Assert.Equal(15, (await history.GetRecentAsync(100)).Count);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task NonFiniteSensorValuesRoundTripAsExplicitJsonStrings(double value)
    {
        var reading = new TelemetryReading("faulty-sensor", Time, value, value, 9);
        var expected = new TelemetryAlert(TelemetryAlertType.OutOfOrderSequence,
            reading.DeviceId, Time, "Sequence 9 arrived after 10.", reading);
        await using (var history = new JsonAlertHistory(HistoryPath))
        {
            await history.AppendAsync(expected);
        }
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(HistoryPath));
        var savedReading = document.RootElement[0].GetProperty("reading");
        Assert.Equal(JsonValueKind.String, savedReading.GetProperty("pressure").ValueKind);
        await using var reopened = new JsonAlertHistory(HistoryPath);
        var actual = Assert.Single(await reopened.GetRecentAsync());
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task FaultySensorDoesNotStopSequenceAlertPersistence_OrFollowingReadings()
    {
        await using var history = new JsonAlertHistory(HistoryPath);
        await using var pipeline = new TelemetryPipeline(new TelemetryPipelineOptions(),
            (alert, _) => history.AppendAsync(alert));
        pipeline.Start();
        var now = DateTimeOffset.UtcNow;
        await pipeline.SubmitAsync(new TelemetryReading("faulty-sensor", now, 90, 70, 10));
        await pipeline.SubmitAsync(new TelemetryReading("faulty-sensor", now, double.NaN, double.PositiveInfinity, 9));
        await pipeline.SubmitAsync(new TelemetryReading("faulty-sensor", now, 125.6789, 70, 11));
        await pipeline.StopAsync();
        var alerts = await history.GetRecentAsync();
        Assert.Equal(2, alerts.Count);
        Assert.Equal(125.6789, alerts[0].Reading!.Pressure); // Display rounding does not alter storage.
        Assert.True(double.IsNaN(alerts[1].Reading!.Pressure));
        Assert.Equal(double.PositiveInfinity, alerts[1].Reading!.Temperature);
    }

    [Fact]
    public async Task RecentFilterUsesTimestamp_AndLastSavedWinsTies_WithoutDeletingHistory()
    {
        await using var history = new JsonAlertHistory(HistoryPath);
        await history.AppendAsync(Alert("newer", Time.AddHours(1)));
        await history.AppendAsync(Alert("older", Time));
        await history.AppendAsync(Alert("tie-last-saved", Time.AddHours(1)));

        Assert.Equal(new[] { "tie-last-saved", "newer" },
            (await history.GetRecentAsync(2)).Select(a => a.DeviceId));
        Assert.Equal(3, (await history.GetRecentAsync(10)).Count);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(HistoryPath));
        Assert.Equal(3, document.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task ConcurrentAppendsAndReadsDoNotLoseRecords_OrExposeMutableState()
    {
        await using (var history = new JsonAlertHistory(HistoryPath))
        {
            var writes = Enumerable.Range(0, 100)
                .Select(i => Task.Run(async () => await history.AppendAsync(Alert($"pump-{i}"))));
            var reads = Enumerable.Range(0, 20).Select(_ => history.GetRecentAsync(100));
            await Task.WhenAll(writes.Cast<Task>().Concat(reads));
            var snapshot = await history.GetRecentAsync(100);
            Assert.Equal(100, snapshot.Select(a => a.DeviceId).Distinct().Count());
            ((TelemetryAlert[])snapshot)[0] = Alert("mutated-snapshot");
            Assert.DoesNotContain(await history.GetRecentAsync(100), a => a.DeviceId == "mutated-snapshot");
        }

        await using var reopened = new JsonAlertHistory(HistoryPath);
        Assert.Equal(100, (await reopened.GetRecentAsync(100)).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("[{\"type\":\"Unknown\",\"deviceId\":\"p\",\"timestamp\":\"2026-01-01T00:00:00Z\",\"message\":\"x\"}]")]
    [InlineData("[{\"type\":0,\"deviceId\":\"p\",\"timestamp\":\"2026-01-01T00:00:00Z\",\"message\":\"x\"}]")]
    [InlineData("[{\"type\":\"DeviceSilent\",\"deviceId\":\" \",\"timestamp\":\"2026-01-01T00:00:00Z\",\"message\":\"x\"}]")]
    public async Task InvalidFileFailsWithoutOverwritingIt_AndReleasesOwnership(string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
        await File.WriteAllTextAsync(HistoryPath, contents);
        Assert.ThrowsAny<Exception>(() => new JsonAlertHistory(HistoryPath));
        Assert.Equal(contents, await File.ReadAllTextAsync(HistoryPath));
        await File.WriteAllTextAsync(HistoryPath, "[]");
        await using var repaired = new JsonAlertHistory(HistoryPath);
        Assert.Empty(await repaired.GetRecentAsync());
    }

    [Fact]
    public async Task SecondWriterIsRejected_UntilFirstStoreIsDisposed()
    {
        var first = new JsonAlertHistory(HistoryPath);
        try
        {
            Assert.Throws<IOException>(() => new JsonAlertHistory(HistoryPath));
        }
        finally
        {
            await first.DisposeAsync();
        }
        await using var second = new JsonAlertHistory(HistoryPath);
        await second.AppendAsync(Alert());
    }

    [Fact]
    public async Task FailedReplacementPreservesPreviousSnapshot_AndRemovesTemporaryFile()
    {
        await using var history = new JsonAlertHistory(HistoryPath);
        await history.AppendAsync(Alert("saved"));
        var original = await File.ReadAllTextAsync(HistoryPath);
        var backup = HistoryPath + ".saved";
        File.Move(HistoryPath, backup);
        Directory.CreateDirectory(HistoryPath); // Deterministic write failure even when tests run as root.
        try
        {
            await Assert.ThrowsAnyAsync<IOException>(async () => await history.AppendAsync(Alert("failed")));
            Assert.Equal("saved", Assert.Single(await history.GetRecentAsync()).DeviceId);
            Assert.Equal(original, await File.ReadAllTextAsync(backup));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(HistoryPath)!, "*.tmp"));
        }
        finally
        {
            Directory.Delete(HistoryPath);
            File.Move(backup, HistoryPath);
        }
        await history.AppendAsync(Alert("retry"));
        Assert.Equal(2, (await history.GetRecentAsync()).Count);
    }

    [Fact]
    public async Task InvalidRecordPreservesPreviousFileAndMemory()
    {
        await using var history = new JsonAlertHistory(HistoryPath);
        await history.AppendAsync(Alert("saved"));
        var original = await File.ReadAllTextAsync(HistoryPath);
        var invalid = Alert("invalid") with { Threshold = double.NaN };
        await Assert.ThrowsAsync<InvalidDataException>(async () => await history.AppendAsync(invalid));
        Assert.Equal(original, await File.ReadAllTextAsync(HistoryPath));
        Assert.Equal("saved", Assert.Single(await history.GetRecentAsync()).DeviceId);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(HistoryPath)!, "*.tmp"));
    }

    [Fact]
    public async Task CancellationBeforeAppendDoesNotChangeHistory()
    {
        await using var history = new JsonAlertHistory(HistoryPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await history.AppendAsync(Alert(), cancellation.Token));
        Assert.Empty(await history.GetRecentAsync());
        Assert.False(File.Exists(HistoryPath));
    }

    [Fact]
    public async Task DisposedStoreRejectsOperations_AndDisposeIsIdempotent()
    {
        var history = new JsonAlertHistory(HistoryPath);
        await history.DisposeAsync();
        await history.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await history.AppendAsync(Alert()));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => history.GetRecentAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonPositiveLimitIsRejected(int count)
    {
        await using var history = new JsonAlertHistory(HistoryPath);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => history.GetRecentAsync(count));
    }

    [Fact]
    public async Task PipelinePersistsConcurrentReadingAndSilenceAlerts_AndDrainsOnShutdown()
    {
        await using var history = new JsonAlertHistory(HistoryPath);
        await using var pipeline = new TelemetryPipeline(new TelemetryPipelineOptions(),
            (alert, _) => history.AppendAsync(alert));
        await pipeline.ProcessReadingAsync(new TelemetryReading("silent-pump", Time, 90, 70, 1));
        pipeline.Start();
        var now = DateTimeOffset.UtcNow;
        var submissions = Enumerable.Range(1, 40).Select(i => pipeline.SubmitAsync(
            new TelemetryReading($"pump-{i}", now, 125, 70, 1)));
        await Task.WhenAll(submissions.Append(pipeline.CheckSilentDevicesAsync(now).AsTask()));
        await pipeline.StopAsync();
        var alerts = await history.GetRecentAsync(100);
        Assert.Equal(40, alerts.Count(a => a.Type == TelemetryAlertType.HighPressure));
        Assert.Single(alerts, a => a.Type == TelemetryAlertType.DeviceSilent);
        Assert.All(alerts.Where(a => a.Type == TelemetryAlertType.HighPressure), a => Assert.Equal(120, a.Threshold));
        Assert.Equal(5, alerts.Single(a => a.Type == TelemetryAlertType.DeviceSilent).Threshold);
    }

    private static TelemetryAlert Alert(string deviceId = "pump-01", DateTimeOffset? time = null) =>
        new(TelemetryAlertType.HighPressure, deviceId, time ?? Time, "Pressure exceeded threshold.",
            new TelemetryReading(deviceId, time ?? Time, 125.5, 70, 1), Threshold: 120);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
