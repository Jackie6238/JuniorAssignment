using System.Globalization;

namespace PumpTelemetry.Monitor;

public sealed record MonitorArguments(string HistoryFile, int Last, bool HistoryOnly, bool Help)
{
    public const string Usage = """
        Usage: dotnet run --project src/PumpTelemetry.Monitor -- [options]
          --last=N              Show the N most recent alerts (default: 10; positive integer).
          --history-only        Display saved history and exit without starting devices.
          --history             Alias for --history-only.
          --history-file=PATH   JSON file (default: alert-history.json in the working directory).
          --help, -h            Show this help.
        Values may also be separate arguments, e.g. --last 5.
        """;

    public static MonitorArguments Parse(string[] args)
    {
        var file = "alert-history.json";
        var last = 10;
        var historyOnly = false;
        var help = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var separator = arg.IndexOf('=');
            var name = separator >= 0 ? arg[..separator] : arg;
            var inlineValue = separator >= 0 ? arg[(separator + 1)..] : null;

            string Value()
            {
                if (inlineValue is not null)
                {
                    return inlineValue;
                }
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    return args[++i];
                }
                throw new ArgumentException($"Option {name} requires a value.");
            }

            switch (name)
            {
                case "--history-only" or "--history" when inlineValue is null:
                    historyOnly = true;
                    break;
                case "--help" or "-h" when inlineValue is null:
                    help = true;
                    break;
                case "--last":
                    if (!int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out last) || last <= 0)
                    {
                        throw new ArgumentException("--last must be a positive integer.");
                    }
                    break;
                case "--history-file":
                    file = Value();
                    ArgumentException.ThrowIfNullOrWhiteSpace(file);
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {arg}");
            }
        }

        return new MonitorArguments(file, last, historyOnly, help);
    }
}
