# Insurance claims backend

A .NET 10 API for creating, reading, and deleting insurance claims and covers.
Claims and covers are stored in MongoDB; audit records are stored in SQL Server.

## Prerequisites and setup

- .NET 10 SDK compatible with `global.json`.
- A running Docker daemon for the application and Docker-backed tests.

From the repository root:

```sh
dotnet restore
dotnet build --no-restore
```

## Run the application

```sh
dotnet run --project Claims --launch-profile Claims
```

Startup launches MongoDB and SQL Server through Testcontainers and applies the
existing audit migrations. Swagger is available at
[https://localhost:7052/swagger](https://localhost:7052/swagger).
If needed, trust the local HTTPS certificate with `dotnet dev-certs https --trust`.

## Run the tests

Docker-free service, validation, premium, and HTTP tests:

```sh
dotnet test --no-restore --filter-class 'Claims.Tests.Unit.*'
```

Docker-backed SQL audit persistence and application smoke tests:

```sh
dotnet test --no-restore --filter-class 'Claims.Tests.Integration.*' --filter-class Claims.Tests.ClaimsControllerTests
```

All tests:

```sh
dotnet test --no-restore
```

## Validation and responses

Claims require a valid cover, a damage cost from 0 to 100000, and a created
timestamp within that cover's period. Covers may start today in UTC or later,
must end after they start, and may last at most one calendar year. The same cover
rules apply to premium quotes. Send ISO 8601 dates with `Z` or an explicit offset;
dates without a timezone are interpreted as UTC.

Invalid submissions return 400 with field errors. Missing claim or cover GET and
DELETE requests return 404. See the technical documentation for boundary rules,
request-model requirements, error examples, and known limitations. Premium
calculation still preserves the legacy defects pending Task 5.

## Asynchronous auditing

Successful MongoDB writes enqueue audit events for a background SQL writer.
DELETE now finishes before its audit event is published. Requests wait for queue
acceptance, not SQL persistence. The queue holds 1000 events and applies
backpressure when full; failed writes get up to three attempts with 1- and 2-second
delays. Shutdown attempts to drain the queue until the host deadline. Events are
in memory and can be lost on crashes or exhausted retries. See the architecture
document for consistency and shutdown limits.

## Documentation

- [Original assignment](docs/README.md)
- [Architecture, testing strategy, and limitations](docs/ARCHITECTURE.md)
