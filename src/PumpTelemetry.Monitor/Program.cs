using System.Text.Json;
using System.Threading.Channels;
using PumpTelemetry.Core;
using PumpTelemetry.Monitor;

try
{
    var arguments = MonitorArguments.Parse(args);
    if (arguments.Help)
    {
        Console.WriteLine(MonitorArguments.Usage);
        return 0;
    }

    await using var history = new JsonAlertHistory(arguments.HistoryFile);
    HistoryDisplay.Write(Console.Out, await history.GetRecentAsync(arguments.Last));
    if (arguments.HistoryOnly)
    {
        return 0;
    }

    var options = new TelemetryPipelineOptions
    {
        PressureThreshold = 120,
        SilentTimeout = TimeSpan.FromSeconds(5),
        SilentCheckInterval = TimeSpan.FromSeconds(1)
    };

    var consoleLock = new object();
    await using var pipeline = new TelemetryPipeline(options, async (alert, _) =>
    {
        // Do not cancel an accepted alert's persistence during Ctrl+C shutdown.
        await history.AppendAsync(alert).ConfigureAwait(false);
        lock (consoleLock)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            try
            {
                Console.WriteLine(
                    $"[{alert.Timestamp:HH:mm:ss.fff}] {alert.Type} {alert.DeviceId}: {alert.Message}");
            }
            finally
            {
                Console.ResetColor();
            }
        }
    });

    using var shutdown = new CancellationTokenSource();
    ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };
    Console.CancelKeyPress += onCancel;
    try
    {
        pipeline.Start(shutdown.Token);
        Console.WriteLine("Pump telemetry monitor running. Press Ctrl+C to stop.");

        var deviceTasks = Enumerable.Range(1, 5)
            .Select(i => new DeviceSimulator($"pump-{i:00}", seed: i * 17))
            .Select(device => Task.Run(
                () => device.RunAsync((reading, ct) => new ValueTask(pipeline.SubmitAsync(reading, ct)), shutdown.Token),
                CancellationToken.None))
            .ToArray();

        try
        {
            await Task.WhenAll(deviceTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
            // StopAsync below propagates any persistence failure that closed the channel.
        }
        finally
        {
            await pipeline.StopAsync().ConfigureAwait(false);
        }
    }
    finally
    {
        Console.CancelKeyPress -= onCancel;
    }

    return 0;
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine(MonitorArguments.Usage);
    return 2;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
{
    Console.Error.WriteLine($"Alert history error: {exception.Message}");
    Console.Error.WriteLine("Monitoring stopped. Check the history path, permissions, JSON contents, and other running instances. Existing history has not been reset.");
    return 1;
}
