# SerialBridge review guidance

SerialBridge is a Windows .NET 8 WPF application that bridges local serial ports to TCP sockets bidirectionally. The existing build and unit-test workflow remains the source of mechanical validation.

## Review output language

- Write all Codex Code Review summaries, findings, severity explanations, risk assessments, and suggested fixes in Simplified Chinese (简体中文).
- Keep code identifiers, file paths, command names, API names, and literal error messages unchanged when precision requires it.
- Apply this language requirement to both automatic pull-request reviews and reviews requested with `@codex review`.

## Code Review Rules

### Transparent serial/TCP forwarding
- Flag changes that corrupt, drop, duplicate, reorder, or unexpectedly transform bytes in either direction, including zero bytes and partial reads/writes. Preserve binary-transparent delivery and the documented single-client versus multi-client behavior.

### Concurrent clients and lifecycle
- Flag actionable races, deadlocks, leaked ports/sockets/tasks, unsafe cancellation or disposal, and reconnect paths that leave duplicate listeners or stale sessions. Serial writes from concurrent TCP clients must remain serialized; stopping one mapping must not disrupt others.

### Network exposure and configuration
- Flag changes that unexpectedly expose listeners outside the configured interface, bypass configured access restrictions, or break persistence and upgrades of serial/TCP mapping settings. Raw TCP has no built-in authentication or encryption, so do not imply otherwise.

Review only meaningful regressions introduced by a pull request. Provide a clear affected path and practical corrective action. Do not commit or generate test scripts, diagnostic code, test-only files, or explanatory documents unless the user explicitly authorizes them.
