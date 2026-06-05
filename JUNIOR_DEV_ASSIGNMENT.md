# Junior Developer Interview Assignment

## Overview

This assignment adds an **Alert History Dashboard** feature to the existing Pump Telemetry Monitor application.

The main goal is to persist generated alerts and allow the monitor app to display a history of past alerts. This improves observability and adds a useful end-user feature while keeping the work scoped for a junior developer.

## Requirements

Implement the following:

1. Persist telemetry alerts to a local file using JSON or CSV.
2. Load saved alert history when the monitor starts.
3. Display a list of recent alerts in the monitor output.
4. Include at least the following fields for each alert:
   - timestamp
   - alert type or severity
   - device or pump ID
   - sensor value or threshold
5. Provide a simple filter option to narrow history by severity, time range, or recent entries.

## What to Change

- Extend the existing alert model or add a new `AlertRecord` type for persistence.
- Add a persistence layer that writes alerts to disk and reads them back.
- Update the monitor application (`PumpTelemetry.Monitor`) so alerts are persisted as they are generated.
- Add an option to display alert history on startup or when requested.
- Add a CLI argument or console command that filters history by severity or recency.

## Deliverables

Please submit a pull request containing:

- The implemented alert history persistence component.
- Changes to the monitor app that save alerts and load history.
- A way to display recent alert history.
- A short README update or documentation note describing how to use the feature.
- Tests covering the new persistence and history display behavior.
- The pull request itself should include a clear description of the changes and any assumptions.

## Interview Guidance

- This is intended to be a **4-hour assignment**.
- You may use AI assistance to help design, write, and document the solution.
- Focus on a small, working implementation rather than a large, polished product.
- Keep the design simple and explain any assumptions in the pull request description.

## Suggested Approach

- Review the current `TelemetryAlert`, `TelemetryPipeline`, and monitor application code.
- Add a storage class for alert history.
- Persist alerts as they are created and load them on startup.
- Render history in console output or a minimal dashboard.
- Add a filter option such as `--severity=high` or `--last=10`.

## Success Criteria

A complete solution should:

- Persist alerts to disk.
- Load and display alert history.
- Support a basic filter mechanism.
- Include documentation and tests.
- Be submitted as a pull request with a clear description.

