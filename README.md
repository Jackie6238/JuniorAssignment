# Pump Telemetry Monitor

A small C#/.NET application that simulates telemetry from multiple industrial pump devices and detects abnormal operating conditions.

## How to run

Requires the .NET 8 SDK or later.

```powershell
dotnet run --project src/PumpTelemetry.Monitor
```

Press `Ctrl+C` to stop the monitor gracefully.

## How to test

```powershell
dotnet test
```

## Architecture choices

The solution is split into:

- `PumpTelemetry.Core`: telemetry models, simulated device producers, processing pipeline, and alert detection.
- `PumpTelemetry.Monitor`: console entry point that wires devices, pipeline, and console alert output together.
- `PumpTelemetry.Tests`: xUnit tests for threshold, sequence, silence, and concurrent submission behavior.

The console app starts five simulated devices. Each device emits a `TelemetryReading` every 100-500ms with a device id, timestamp, pressure, temperature, and sequence number. The simulator occasionally produces a pressure spike and an out-of-order sequence so the alert paths can be seen during a short run.

## Concurrency approach

The processing pipeline uses a bounded `Channel<TelemetryReading>` as an asynchronous fan-in queue. Multiple device tasks can publish concurrently, and the channel capacity provides backpressure if producers outpace alert processing for long enough. A single reader consumes the channel so per-device sequence checks are deterministic and based on channel arrival order.

Shutdown is idempotent: completing the channel writer and cancelling the silent-device monitor is safe to request more than once. Queued readings drain before shutdown completes. The silent-device monitor runs on a `PeriodicTimer` and is cancelled during shutdown.

## Alerts

The pipeline emits console alerts for:

- pressure above the configured threshold,
- out-of-order sequence numbers,
- devices silent for more than 5 seconds.

Silent-device detection starts tracking a device after its first reading, because the monitor has no external device registry in this small assignment.

## Alert history

The monitor loads `alert-history.json` from the **current working directory** and
prints the ten most recent saved alerts before starting the original five devices.
Each new alert is saved before its live console message is printed. A missing file
starts an empty history; the JSON file is created on the first alert.

Build and run from the repository root with the .NET 8 SDK:

```sh
dotnet build -c Release
dotnet run --project src/PumpTelemetry.Monitor
```

Choose the number of recent entries, inspect history without starting the simulator,
or use a different file:

```sh
dotnet run --project src/PumpTelemetry.Monitor -- --last=5
dotnet run --project src/PumpTelemetry.Monitor -- --history-only --last=20
dotnet run --project src/PumpTelemetry.Monitor -- --history-file="data/pump alerts.json" --last=5
dotnet run --project src/PumpTelemetry.Monitor -- --help
```

`--history` is an alias for `--history-only`; `-h` is an alias for `--help`.
Values can also be separate arguments, for example `--last 5` or
`--history-file "data/pump alerts.json"`.

`--last=N` must be a positive integer. It limits **display only**, including when
used without `--history-only`; all generated alerts are still retained. Results
are sorted by timestamp descending, with the last saved record first on a tie.
No severity classification is invented: the existing alert **type** is stored,
and recency is the supported filter.

The file is a readable JSON array with camelCase property names and string alert
types. Every record contains `timestamp`, `type`, `deviceId`, and `message`.
Pressure alerts retain their full `reading` and pressure `threshold`; sequence
alerts retain the incoming sequence in `reading.sequenceNumber` and the previous
high-water mark in `message`; silence alerts retain their timeout `threshold`
in **seconds** and last-seen timestamp in `message` (there is no sensor reading).
Pressure is rounded to one decimal place for console display; its original
precision is retained in the JSON. Non-finite sensor values are preserved as
explicit JSON strings (`"NaN"`, `"Infinity"`, `"-Infinity"`) and restored as doubles
on load. This prevents a sequence alert containing a faulty sensor reading from
breaking persistence. These strings describe measurements, not valid numeric
thresholds. For example:

```json
[
  {
    "type": "DeviceSilent",
    "deviceId": "pump-01",
    "timestamp": "2026-01-02T03:04:11+00:00",
    "message": "Device has been silent since 2026-01-02T03:04:05.0000000+00:00.",
    "reading": null,
    "threshold": 5
  }
]
```

### Safety and scope

- `JsonAlertHistory` serializes appends and snapshot reads with a `SemaphoreSlim`.
  The pipeline's reading processor and silence timer can both emit alerts.
- An append writes the complete updated array to a unique temporary file **in the
  same directory**, flushes it to disk, then replaces the destination. Memory is
  updated only after replacement succeeds. Failed writes preserve the previous
  committed history and clean up their temporary file when possible.
- A separate `<history-file>.lock` is held exclusively for the store's lifetime,
  including `--history-only`. A second monitor using that path exits with an error.
  The empty lock file remains after exit; its presence does not mean it is locked.
  Do not delete it or edit/replace the JSON file while a monitor is running.
- Malformed JSON, missing required fields, invalid alert types, or invalid records
  stop startup with a clear error. The file is **not** silently reset. Back it up,
  then repair it or select a different path before restarting. The containing
  directory must be writable, including for history-only access.
- Write failures stop monitoring and release blocked producers rather than silently
  dropping alerts. Exit codes: `0` normal/help/history-only, `1` history storage or
  data error, `2` invalid command-line arguments.
- Ctrl+C cancels device production and the silence timer; queued readings drain,
  and persistence of accepted alerts finishes before the store is disposed.
- This deliberately uses an in-memory list and rewrites the array on each append:
  O(n) write cost and O(n) memory. It suits this small local assignment, not an
  unbounded production stream. A database or append-only format would be a next
  step for large histories. There is no rotation, automatic repair, external-editor
  coordination, or guarantee against every filesystem/power-loss failure. A forced
  kill can leave an unused `.tmp` file, which startup ignores.

### Verification

```sh
dotnet test
dotnet build -c Release -warnaserror
dotnet test -c Release --no-build
```

The tests cover persistence/restart for all three alert types, concurrent appends
and reads, recency sorting and ties, failed replacement, invalid appends, non-finite sensor values,
malformed/invalid history, cancellation, exclusive file ownership, real CLI
processes and output, and pipeline failure/shutdown behavior. Existing detection
and concurrent-submission tests remain in place, with additional threshold,
silence-reset, and sequence-high-water-mark regressions.

For a manual end-to-end check, run the monitor until alerts appear, press Ctrl+C,
then run `--history-only --last=2` against the same file. The history should contain
the saved alerts and the command should exit without starting any devices.
