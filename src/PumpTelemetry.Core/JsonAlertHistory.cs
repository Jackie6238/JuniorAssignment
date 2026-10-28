using System.Text.Json;
using System.Text.Json.Serialization;

namespace PumpTelemetry.Core;

public sealed class JsonAlertHistory : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        // Preserve faulty sensor values as JSON strings ("NaN", "Infinity", "-Infinity").
        // Without this, a valid sequence alert containing such a reading cannot be saved.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter<TelemetryAlertType>(allowIntegerValues: false) }
    };

    private readonly string _path;
    private readonly FileStream _fileLock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<TelemetryAlert> _alerts;
    private bool _disposed;

    public JsonAlertHistory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        // Hold a separate lock file because replacing the JSON file changes its inode.
        // Leave the lock file in place on exit; deleting it could let writers bypass a lock.
        _fileLock = new FileStream(_path + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (Directory.Exists(_path))
            {
                throw new IOException("The alert history path points to a directory, not a JSON file.");
            }

            if (File.Exists(_path))
            {
                using var stream = File.OpenRead(_path);
                _alerts = JsonSerializer.Deserialize<List<TelemetryAlert>>(stream, JsonOptions)
                    ?? throw new InvalidDataException("Alert history must be a JSON array, not null.");
                foreach (var alert in _alerts)
                {
                    Validate(alert);
                }
            }
            else
            {
                _alerts = new List<TelemetryAlert>();
            }
        }
        catch
        {
            _fileLock.Dispose();
            throw;
        }
    }

    public async ValueTask AppendAsync(TelemetryAlert alert, CancellationToken cancellationToken = default)
    {
        Validate(alert);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var updated = new List<TelemetryAlert>(_alerts) { alert };
            var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                // Once a write begins, finish it even if shutdown is requested.
                // The old file remains intact until the complete replacement is ready.
                await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, updated, JsonOptions).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, _path, overwrite: true);
                _alerts = updated;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TelemetryAlert>> GetRecentAsync(int count = 10)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Timestamps may arrive out of order. For ties, show the last saved first.
            return _alerts.AsEnumerable().Reverse().OrderByDescending(alert => alert.Timestamp)
                .Take(count).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Validate(TelemetryAlert? alert)
    {
        if (alert is null || !Enum.IsDefined(alert.Type)
            || string.IsNullOrWhiteSpace(alert.DeviceId) || string.IsNullOrWhiteSpace(alert.Message)
            || (alert.Threshold.HasValue && !double.IsFinite(alert.Threshold.Value)))
        {
            throw new InvalidDataException("Alert history contains an invalid alert record.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_disposed)
            {
                _disposed = true;
                _fileLock.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
