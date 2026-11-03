# Architecture

## Overview

The API stays in one .NET 10 project. Dependencies flow from controllers to
application services, then to repositories and auditing. API and persistence
models remain shared; MongoDB mappings and SQL migrations are unchanged.

## Responsibilities

| Component | Responsibility |
| --- | --- |
| `Claims/Controllers` | HTTP binding and successful responses through service interfaces. |
| `Claims/Services` | Business validation, existence checks, ID generation, premium calculation, and audit ordering. |
| `PremiumCalculator` | Arithmetic only; the legacy algorithm is unchanged. |
| `Claims/Repositories` | Focused claim and cover queries and writes, including direct ID lookups. |
| `Claims/Data/ClaimsContext.cs` | Existing MongoDB collection mappings. |
| `Claims/Auditing` | `IAuditer` and synchronous SQL Server writes using `AuditContext`. |
| `Claims/Filters` | Translate business validation and missing-resource exceptions into HTTP responses. |
| `Claims/Serialization` | UTC JSON parsing for claim and cover dates. |
| `Claims/ModelBinding` | UTC parsing for the premium endpoint's query dates. |
| `Claims/Program.cs` | Dependency registration and application startup. |

## Dependency injection

Services, repositories, auditing, and database contexts are scoped to the request.
The stateless premium calculator and `TimeProvider.System` are singletons. Cover
validation reads the UTC date through `TimeProvider`; tests use a fixed clock.

Interfaces allow small test fakes without databases. There is no generic
repository, additional unit-of-work abstraction, or separate project per layer.
Service-level validation uses ordinary checks, without a validation package.

## Claim validation

Before changing the submitted object, assigning an ID, saving, or auditing,
`ClaimService` checks:

- `DamageCost` is between 0 and 100000, inclusive.
- `CoverId` is not blank and identifies an existing cover.
- The normalized `Created` timestamp is between the cover's normalized
  `StartDate` and `EndDate`, including both boundaries.

A missing referenced cover is a 400 field error on `coverId`, because the claim
submission contains an invalid reference. It is distinct from requesting a missing
resource by its own URL. Claim dates are not independently checked against now.

## Cover validation

Creation and premium quoting share `CoverService.ValidatePeriod`:

- `StartDate` must not precede today's UTC date. Any time today is accepted,
  including a time earlier than the current time.
- `EndDate` must be strictly after `StartDate`.
- `EndDate` must not exceed `StartDate.AddYears(1)`, preserving the time of day.

One calendar year can span 366 days. February 29, 2032 maps to February 28, 2033
under `AddYears(1)`. One tick beyond the anniversary is rejected. Year 9999 is
handled without overflowing `AddYears`; representable later dates in that year
cannot exceed a full calendar year.

Validation completes before premium calculation or changes to the submitted cover.
Invalid creation leaves its ID, dates, and premium unchanged and does not save or
audit. Invalid quotes do not invoke the calculator, save, or audit.

## UTC contract

- JSON dates use ISO 8601. Explicit offsets are converted to the same UTC instant;
  date-only and timezone-free values are interpreted as UTC.
- Compute query dates use invariant .NET parsing with `AssumeUniversal`, then
  convert to UTC. Encode a positive offset's `+` as `%2B` in a URL.
- Services accept `DateTimeKind.Utc` and treat `Unspecified` as UTC. They reject
  `Local` values rather than using the server's timezone. JSON and query parsing
  normalize explicit offsets before calling services.
- Successfully created claim and cover dates are stored as UTC. Existing cover
  dates are normalized for comparison without modifying the retrieved cover.
- Coverage boundaries are exact timestamps. An `EndDate` at midnight does not
  imply coverage through the remainder of that calendar day.

For example, `2030-01-01T02:00:00+02:00` and
`2030-01-01T00:00:00Z` are equivalent. UTC conversion happens before validation,
so a wall-clock date of today can still normalize to yesterday and be rejected.

## HTTP errors and operation order

`BusinessValidationException` carries field errors without HTTP dependencies.
`ValidationExceptionFilter` returns 400 `ValidationProblemDetails` with
`application/problem+json`:

```json
{
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "damageCost": ["DamageCost must be between 0 and 100000, inclusive."]
  }
}
```

Malformed JSON/query dates and framework model-validation failures also return
400 validation problem details through `[ApiController]`. Business errors use
camel-case field keys; framework errors may use CLR property names or JSON paths.
The exact framework error text is not part of the application contract.

Service lookups throw `ResourceNotFoundException` when a claim or cover does not
exist. `ResourceNotFoundExceptionFilter` returns 404 `ProblemDetails`:

```json
{
  "title": "Resource not found.",
  "status": 404,
  "detail": "Claim 'missing' was not found."
}
```

| Operation | Successful response | Missing or invalid input |
| --- | --- | --- |
| GET collection | 200 with an array, including `[]` | Empty collections are not 404. |
| GET by ID | 200 with the entity | 404 if missing. |
| POST creation | 200 with server-generated ID and created entity | 400 for validation failure. |
| POST premium compute | 200 with a decimal premium | 400 for invalid dates or period. |
| DELETE by ID | 200 with an empty body | 404 if missing. |

Valid creates save, then audit; cover creation replaces the submitted premium.
Deletes check existence, then audit, then delete. Missing resources cause neither
an audit nor a repository delete call. Repeated DELETE returns 404 after the first
successful deletion. Unexpected infrastructure failures are not converted to 400
or 404 by these filters.

## Current premium calculation

The calculator preserves the legacy algorithm, including its defects:

- The base daily rate is 1250 with multipliers: Yacht 1.1, PassengerShip 1.2,
  Tanker 1.5, and other types 1.3.
- Conditions overlap: the first 30 days receive three charges per day, days
  31–180 receive two, and days 181–365 receive one.
- Overlapping discounted charges use 5% and 8% for yachts, and 2% and 3% otherwise.
- Partial days round up; days after day 365 add no premium.
- Direct calculator calls for zero-length or reversed periods return zero, but
  the service/API now reject such periods before calling it.

A one-day yacht cover currently returns 3946.25; the assignment's correct value
is 1375. Characterization tests preserve the current results until Task 5.

## Testing strategy

The xUnit project uses fake repositories, a recording auditor, and a fixed
`TimeProvider`. It covers validation boundaries, leap years, UTC midnight,
normalization, missing resources, ID generation, premium assignment, ordering,
awaiting persistence/lookups, and failure paths. Invalid-operation tests verify
no persistence or auditing and no service mutation of submitted values.

HTTP tests use an in-memory ASP.NET Core server with real controllers/services
and fake repositories/auditing. They verify binding, JSON string enums, 400/404
problem details, successful responses, and empty collections. Offset tests include
crossing UTC date and coverage boundaries. No additional testing framework or
mocking package was introduced.

Run Docker-free tests from the repository root:

```sh
dotnet test --no-restore --filter-class 'Claims.Tests.Unit.*'
```

Run the original integration smoke test separately:

```sh
dotnet test --no-restore --filter-class Claims.Tests.ClaimsControllerTests
```

The integration test starts `Program.cs`, launches SQL Server and MongoDB through
Testcontainers, applies existing audit migrations, and checks that `GET /Claims`
succeeds. It requires a running Docker daemon. Run `dotnet test --no-restore` for
all tests. See the [project README](../README.md) for setup and build commands.

## Known limitations and trade-offs

- Auditing still writes synchronously during requests. A failed audit after
  creation leaves the record saved; an audit failure before deletion prevents
  deletion. An audit can remain if the subsequent deletion fails. Background
  auditing remains Task 3; premium fixes remain Task 5.
- Existence checks and writes are separate operations. Concurrent cover deletion
  can race with claim creation; concurrent resource deletion can race with the
  pre-delete check. MongoDB and SQL Server have no shared transaction. Cover
  references are checked at creation; deleting a cover does not cascade to claims.
- DELETE performs both a service existence lookup and the repository's existing
  lookup to preserve repository contracts.
- Shared non-nullable string models retain ASP.NET Core's implicit required-field
  validation. Create bodies still need an `id` value, even though the service
  replaces it; claims also require `name`. Introducing create-specific DTOs is
  outside this change.
- Query parsing accepts more invariant .NET date formats than JSON. Clients should
  send complete ISO 8601 dates/timestamps, rather than partial time-only inputs.
- Docker-free tests do not verify database mappings, actual writes, or SQL audit
  records. The original integration test covers only claims-list startup/access,
  not complete persistence flows, concurrency, or failure recovery.
- The in-memory HTTP host mirrors relevant registrations and JSON settings rather
  than exercising the full production startup and middleware pipeline.
