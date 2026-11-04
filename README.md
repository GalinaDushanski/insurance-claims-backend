# Insurance claims backend

A .NET 10 HTTP API for creating, reading, and deleting insurance claims and covers.
Claims and covers are stored in MongoDB; a background worker writes audit history
to SQL Server. This repository implements the five interview tasks and runs locally
with Docker. Cloud hosting is not required for the demo.

## Prerequisites and setup

- A .NET 10 SDK compatible with `global.json`.
- Docker Desktop or another running Docker daemon. The application and database
  integration tests start containers automatically; no manual database setup is needed.
- Access to the MongoDB and SQL Server container images on the first run.

Run commands from the repository root:

```sh
dotnet restore
dotnet build --no-restore
```

## Run the application

```sh
dotnet run --project Claims --launch-profile Claims
```

Startup launches MongoDB and SQL Server through Testcontainers and applies the
existing SQL migrations. The development profile exposes Swagger at
`https://localhost:7052/swagger`. If the local HTTPS certificate is not trusted,
run `dotnet dev-certs https --trust`.

The database instances are temporary containers, not an externally managed store.
Do not rely on their data surviving a new application session.

## Implementation

- **Task 1:** Thin controllers call application services; focused repositories own
  MongoDB access. Dependencies use constructor injection within one API project.
- **Task 2:** Services validate claim costs, cover references, and coverage dates.
  Invalid submissions return 400 field errors; missing resources return 404.
- **Task 3:** Successful mutations publish audit events to a bounded in-memory
  queue. A background worker writes SQL records with retries.
- **Task 4:** Unit tests cover business rules and failure paths, alongside HTTP
  and database integration tests.
- **Task 5:** Premium calculation uses three progressive pricing tiers, with
  explicit partial-day billing and tests for the corrected totals.

## Run the tests

Docker-free service, pricing, component, and in-process HTTP tests:

```sh
dotnet test --no-restore --filter-class 'Claims.Tests.Unit.*'
```

Docker-backed SQL auditing, MongoDB precision, and application smoke tests:

```sh
dotnet test --no-restore --filter-class 'Claims.Tests.Integration.*' --filter-class Claims.Tests.ClaimsControllerTests
```

All tests:

```sh
dotnet test --no-restore
```

Tests use the Microsoft.Testing.Platform runner configured in `global.json`.
The `Unit` namespace selects the Docker-free suite, including HTTP/component tests.

## Documentation and limitations

Start with the [architecture and reviewer guide](docs/ARCHITECTURE.md) for code
entry points, date and pricing assumptions, and testing boundaries. The
[original assignment](docs/README.md) is preserved unchanged.

Known limitations include audit event loss on crashes, confirmed MongoDB
sub-millisecond date precision loss, and unresolved dependency security warnings.
The architecture document explains their impact; the precision tests reproduce
the limitation rather than fixing it.
