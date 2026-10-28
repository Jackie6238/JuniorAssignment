using PumpTelemetry.Core;
using PumpTelemetry.Monitor;
using Xunit;

namespace PumpTelemetry.Tests;

public sealed class HistoryDisplayTests
{
    [Fact]
    public void EmptyHistoryHasClearMessage()
    {
        using var output = new StringWriter();
        HistoryDisplay.Write(output, Array.Empty<TelemetryAlert>());
        Assert.Contains("Alert history (0, newest first):", output.ToString());
        Assert.Contains("No saved alerts.", output.ToString());
    }

    [Fact]
    public void DisplayIncludesTimestampTypeDeviceAndValues_ForEveryAlertType()
    {
        var time = DateTimeOffset.Parse("2026-01-02T03:04:05Z");
        var alerts = new[]
        {
            new TelemetryAlert(TelemetryAlertType.HighPressure, "pressure-pump", time, "High pressure.",
                new TelemetryReading("pressure-pump", time, 125.54321, 70, 1), Threshold: 120),
            new TelemetryAlert(TelemetryAlertType.OutOfOrderSequence, "sequence-pump", time, "Sequence 9 arrived after 10.",
                new TelemetryReading("sequence-pump", time, 90, 70, 9)),
            new TelemetryAlert(TelemetryAlertType.DeviceSilent, "silent-pump", time, "Device silent.", Threshold: 5)
        };
        using var output = new StringWriter();
        HistoryDisplay.Write(output, alerts);
        var text = output.ToString();
        Assert.Contains("2026-01-02T03:04:05.0000000+00:00", text);
        Assert.Contains("HighPressure pressure-pump: pressure=125.5 threshold=120", text);
        Assert.Contains("OutOfOrderSequence sequence-pump: sequence=9", text);
        Assert.Contains("Sequence 9 arrived after 10.", text);
        Assert.Contains("DeviceSilent silent-pump: threshold=5s", text);
    }

    [Fact]
    public void ArgumentsHaveSimpleDefaults_AndSupportCombinedOptions()
    {
        Assert.Equal(new MonitorArguments("alert-history.json", 10, false, false), MonitorArguments.Parse([]));
        Assert.Equal(new MonitorArguments("a folder/history.json", 3, true, false),
            MonitorArguments.Parse(["--last=3", "--history-only", "--history-file=a folder/history.json"]));
        Assert.True(MonitorArguments.Parse(["--help"]).Help);
    }

    [Theory]
    [InlineData("--history")]
    [InlineData("--history-only")]
    public void HistoryAliasesSelectHistoryOnly(string option)
    {
        Assert.True(MonitorArguments.Parse([option]).HistoryOnly);
    }

    [Fact]
    public void ArgumentsAcceptSeparateValues_AndShortHelp()
    {
        Assert.Equal(new MonitorArguments("a folder/history.json", 3, true, false),
            MonitorArguments.Parse(["--last", "3", "--history-file", "a folder/history.json", "--history"]));
        Assert.True(MonitorArguments.Parse(["-h"]).Help);
    }

    [Theory]
    [InlineData("--last", "--history")]
    [InlineData("--last", "-h")]
    [InlineData("--history-file", "--last=2")]
    public void MissingValueBeforeAnotherOptionIsRejected(string option, string followingOption)
    {
        Assert.Throws<ArgumentException>(() => MonitorArguments.Parse([option, followingOption]));
    }

    [Theory]
    [InlineData("--history=yes")]
    [InlineData("--history-only=true")]
    [InlineData("--help=yes")]
    [InlineData("-h=true")]
    public void BooleanOptionsRejectValues(string option)
    {
        Assert.Throws<ArgumentException>(() => MonitorArguments.Parse([option]));
    }

    [Theory]
    [InlineData("--last=0")]
    [InlineData("--last=-1")]
    [InlineData("--last=abc")]
    [InlineData("--last=1.2")]
    [InlineData("--last=2147483648")]
    [InlineData("--last=")]
    [InlineData("--last")]
    [InlineData("--history-file=")]
    [InlineData("--unknown")]
    public void InvalidArgumentsAreRejected(string option)
    {
        Assert.ThrowsAny<ArgumentException>(() => MonitorArguments.Parse([option]));
    }
}
