# Insurance claims backend

A .NET 10 API for creating, reading, and deleting insurance claims and covers.
Claims and covers are stored in MongoDB; audit records are stored in SQL Server.

## Prerequisites and setup

- .NET 10 SDK compatible with `global.json`.
- A running Docker daemon for the application and integration test.

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

Docker-free service, premium, and HTTP characterization tests:

```sh
dotnet test --no-restore --filter-class 'Claims.Tests.Unit.*'
```

All tests, including the Docker integration test:

```sh
dotnet test --no-restore
```

## Documentation

- [Original assignment](docs/README.md)
- [Architecture, testing strategy, and limitations](docs/ARCHITECTURE.md)
