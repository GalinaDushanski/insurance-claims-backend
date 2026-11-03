# Architecture

## Overview

The API stays in one .NET 10 project. Dependencies flow from controllers to
application services, then to repositories and auditing. Domain models remain
shared across these layers to preserve the API and persistence mappings.

## Responsibilities

| Component | Responsibility |
| --- | --- |
| `Claims/Controllers` | HTTP request binding and responses. Each controller depends on its service interface. |
| `Claims/Services` | Claim and cover operations, ID generation, premium calculation, and audit ordering. |
| `PremiumCalculator` | Arithmetic only; no HTTP or database dependencies. |
| `Claims/Repositories` | Focused claim and cover queries and writes through `ClaimsContext`, including direct ID lookups. |
| `Claims/Data/ClaimsContext.cs` | Existing MongoDB collection mappings. |
| `Claims/Auditing` | `IAuditer` and its synchronous SQL Server implementation using `AuditContext`. |
| `Claims/Program.cs` | Dependency registration and application startup. |

Creates generate an ID, save, then audit; cover creation also replaces the
submitted premium. Deletes audit first, even for missing records. Missing-resource
GETs return 204; DELETEs return an empty 200. Creates return 200 with the created
object. Task 1 preserves these behaviors.

## Dependency injection

Services, repositories, auditing, and database contexts are scoped to the request.
This keeps database-dependent components within their contexts' lifetimes. The
calculator is a singleton because it has no state or database dependencies.

Interfaces allow tests to substitute small fakes without starting databases.
Repositories expose only the operations in use; there is no generic repository,
additional unit-of-work abstraction, or separate project for each layer.

## Current premium calculation

The extracted calculator preserves the legacy algorithm, including its defects:

- The base daily rate is 1250, adjusted by cover type: Yacht 1.1, PassengerShip
  1.2, Tanker 1.5, and other types 1.3.
- Discount conditions overlap. The first 30 days receive three charges per day;
  days 31–180 receive two, and days 181–365 receive one.
- The overlapping discounted charges use 5% and 8% for yachts, and 2% and 3%
  for other types.
- Partial days round up to whole-day charges; zero-length and reversed periods
  return zero.
- Days after day 365 add no premium.

For example, a one-day yacht cover currently costs 3946.25. Characterization tests
record compatibility with this behavior, not the desired Task 5 formula. The
calculation will be corrected separately.

## Testing strategy

The existing xUnit project contains service unit tests, premium characterization
tests, HTTP characterization tests, and the original integration smoke test.
No additional testing framework or mocking package was introduced.

Service tests use fake repositories and a recording auditor. They cover generated
IDs, saved and returned objects, premium assignment, reads, missing-resource
deletes, audit arguments and ordering, waiting for persistence, and failure paths.
Premium tests record current totals at day-band boundaries and known defects.

HTTP tests use an in-memory ASP.NET Core server with fake repositories and auditing.
They exercise real controllers, model binding, status codes, and JSON formatting,
including string enums, without running `Program.cs` or starting Docker.

Run these Docker-free tests from the repository root:

```sh
dotnet test --no-restore --filter-class 'Claims.Tests.Unit.*'
```

Run the original Docker integration smoke test separately:

```sh
dotnet test --no-restore --filter-class Claims.Tests.ClaimsControllerTests
```

That test starts the application, which launches SQL Server and MongoDB containers
and applies the existing audit migrations. It requires a running Docker daemon and
verifies that `GET /Claims` succeeds. Run `dotnet test --no-restore` for all tests.
See the [project README](../README.md) for SDK setup, restore, and build commands.

## Known limitations and trade-offs

- Auditing still writes synchronously during the request. An audit failure after
  creation leaves the record saved; an audit failure before deletion prevents
  deletion. MongoDB and SQL Server operations have no shared transaction.
- Task 2 business validation and Task 3 background auditing remain unimplemented.
  The premium defects above remain for Task 5.
- Docker-free tests do not verify production database providers, MongoDB mappings,
  actual repository writes, or SQL audit records. The original integration test
  covers only a successful claims-list request, not complete persistence flows.
- The in-memory HTTP host mirrors relevant registrations and JSON settings. It
  does not exercise the full production startup or middleware pipeline.
- Shared API and persistence models keep this assignment small, at the cost of
  coupling their shapes. No DTO layer was added during the behavior-preserving
  refactor.
