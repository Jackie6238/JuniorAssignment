using System.Diagnostics;
using System.Runtime.InteropServices;
using PumpTelemetry.Core;
using PumpTelemetry.Monitor;
using Xunit;

namespace PumpTelemetry.Tests;

public sealed class MonitorCliTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pump-cli-tests-" + Guid.NewGuid());

    public MonitorCliTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task HistoryOnlyLoadsFiltersAndDisplaysFile_WithoutStartingDevicesOrRewritingIt()
    {
        var path = Path.Combine(_directory, "a folder", "saved.json");
        var time = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        await using (var history = new JsonAlertHistory(path))
        {
            await history.AppendAsync(new TelemetryAlert(TelemetryAlertType.DeviceSilent, "older", time,
                "Old silence.", Threshold: 5));
            await history.AppendAsync(new TelemetryAlert(TelemetryAlertType.HighPressure, "newer", time.AddHours(1),
                "High pressure.", new TelemetryReading("newer", time.AddHours(1), 130, 70, 2), Threshold: 120));
        }
        var original = await File.ReadAllTextAsync(path);
        var result = await RunAsync("--history", "--last", "1", "--history-file", path);
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        Assert.Contains("Alert history (1, newest first):", result.Output);
        Assert.Contains("HighPressure newer: pressure=130.0 threshold=120", result.Output);
        Assert.DoesNotContain("older", result.Output);
        Assert.DoesNotContain("monitor running", result.Output);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task EmptyHistoryOnlyUsesDefaultPathAndExitsSuccessfully()
    {
        var result = await RunAsync("--history-only");
        Assert.Equal(0, result.Code);
        Assert.Contains("No saved alerts.", result.Output);
        Assert.False(File.Exists(Path.Combine(_directory, "alert-history.json")));
    }

    [Theory]
    [InlineData("broken JSON")]
    [InlineData("[null]")]
    [InlineData("null")]
    public async Task CorruptHistoryExitsWithErrorAndPreservesFile(string contents)
    {
        var path = Path.Combine(_directory, "alert-history.json");
        await File.WriteAllTextAsync(path, contents);
        var result = await RunAsync("--history-only");
        Assert.Equal(1, result.Code);
        Assert.Contains("Alert history error:", result.Error);
        Assert.DoesNotContain("monitor running", result.Output);
        Assert.Equal(contents, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DirectoryUsedAsHistoryFileProducesClearError()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "alert-history.json"));
        var result = await RunAsync("--history-only");
        Assert.Equal(1, result.Code);
        Assert.Contains("points to a directory", result.Error);
    }

    [Fact]
    public async Task AnotherProcessCannotOpenHistoryOwnedByRunningStore()
    {
        await using var history = new JsonAlertHistory(Path.Combine(_directory, "alert-history.json"));
        var result = await RunAsync("--history-only");
        Assert.Equal(1, result.Code);
        Assert.Contains("Alert history error:", result.Error);
    }

    [Fact]
    public async Task HelpDoesNotOpenHistory_AndInvalidArgumentHasUsageAndNonzeroExit()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "alert-history.json"), "broken JSON");
        var help = await RunAsync("--help");
        Assert.Equal(0, help.Code);
        Assert.Contains("--last=N", help.Output);
        Assert.Empty(help.Error);
        var invalid = await RunAsync("--last=0");
        Assert.Equal(2, invalid.Code);
        Assert.Contains("--last must be a positive integer", invalid.Error);
        Assert.Contains("Usage:", invalid.Error);
    }

    private async Task<(int Code, string Output, string Error)> RunAsync(params string[] args)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? Path.Combine(new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()).Parent!.Parent!.Parent!.FullName,
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var start = new ProcessStartInfo(host)
        {
            WorkingDirectory = _directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(HistoryDisplay).Assembly.Location);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
