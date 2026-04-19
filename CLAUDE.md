# WindowStream

## Repository layout

- `src/WindowStream.Core/` — multi-targeted library (`net8.0`, `net8.0-windows10.0.19041.0`). Protocol, session, discovery, capture/encode interfaces.
- `src/WindowStreamServer/` — .NET MAUI picker GUI (Windows target in v1).
- `src/WindowStream.Cli/` — headless console application.
- `tests/WindowStream.Core.Tests/` — unit tests (xUnit, Coverlet).
- `tests/WindowStream.Integration.Tests/` — capture/encode smoke tests, Windows-only.

## Build

```bash
dotnet restore
dotnet build
```

## Test (100% line + branch coverage gate)

```bash
dotnet test
```

Coverage thresholds are enforced via `Directory.Build.props` and will fail the
build below 100% line or branch coverage on `WindowStream.Core`.

## Conventions

- One type per file.
- Nullable reference types enabled everywhere.
- Full words in identifiers — `maximum`, `configuration`, `sequence`, `arguments` (no `max`, `cfg`, `seq`, `args`).
- `async`/`await` for I/O; `CancellationToken` threaded through public async methods.
- Commit messages in imperative mood. Small, frequent commits.
