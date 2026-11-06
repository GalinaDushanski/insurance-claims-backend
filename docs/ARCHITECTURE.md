# Architecture and reviewer guide

## Architecture and dependencies

Four .NET 10 production projects provide compile-time boundaries within one
application:

| Project | Responsibility |
| --- | --- |
| `Claims.Api` | Controllers, HTTP DTOs/mapping, JSON handling, exception filters, Swagger, composition root |
| `Claims.Application` | Use cases, validation, repository and audit publisher interfaces |
| `Claims.Domain` | Plain Claim/Cover entities, enums, deterministic premium calculation |
| `Claims.Infrastructure` | MongoDB repositories/mappings, SQL audit storage, queue/worker, local database startup |

Arrows show direct project references:

```text
Claims.Api            → Claims.Application, Claims.Domain, Claims.Infrastructure
Claims.Application    → Claims.Domain
Claims.Infrastructure → Claims.Application, Claims.Domain
Claims.Domain         → none
```

Domain and Application have no external package or framework references.
`Claims.Tests` references all four projects. Existing namespaces are retained;
project membership enforces the boundaries.

[Program.cs](../Claims.Api/Program.cs) composes the application. Its Infrastructure
reference registers adapters and initializes databases; controllers use Application
interfaces. Services, repositories, both DbContexts, and the SQL writer are scoped.
The calculator, clock, audit publisher, and queue are singletons; the worker creates
a fresh scope per SQL write attempt.

Run `dotnet run --project Claims.Api --launch-profile Claims` from the repository
root. Docker is required: startup creates temporary MongoDB/SQL Server containers
and applies migrations. Swagger is at `https://localhost:7052/swagger`.

## SOLID and design decisions

**Single responsibility:** controllers handle HTTP, Application services coordinate
business operations, Domain owns pricing, and Infrastructure handles persistence
and audit delivery. [DTO mapping](../Claims.Api/Contracts/ContractMapping.cs) separates
HTTP contracts from Domain entities. External MongoDB configuration keeps BSON and
JSON attributes out of Domain.

**Dependency inversion:** Application owns `IClaimRepository`, `ICoverRepository`,
and `IAuditer`; Infrastructure implements them. Services depend on these
abstractions, allowing fake or alternate adapters without database dependencies.
Focused interfaces serve current use cases; the solution does not claim a separate
implementation of every SOLID principle.

Validation stays in services, shared by cover creation and quoting. Pricing uses
three explicit tiers rather than a strategy framework; changing rates requires a
code change. MongoDB maps Domain entities directly, avoiding duplicate document
models while keeping storage configuration in Infrastructure.

Existing JSON contracts, BSON field names, SQL schemas, and migration history are
preserved, including string `_id`, `claimType` for both entity types, and migration
`20220728113129_1stMigration`. DTO names retain Swagger schema IDs `Claim` and `Cover`.

## Validation and HTTP behavior

[ClaimService](../Claims.Application/Services/ClaimService.cs) and
[CoverService](../Claims.Application/Services/CoverService.cs) validate before
assigning IDs, saving, or publishing audits:

- `DamageCost`: 0–100000, inclusive.
- `CoverId`: nonblank and identifies an existing cover.
- Claim `Created`: within the cover's exact start/end timestamps, inclusive.
- Cover `StartDate`: today in UTC or later; any time today is accepted.
- `EndDate`: strictly after start and no later than `StartDate.AddYears(1)`.
  Quotes use the same rules. A calendar year can span 366 days; February 29 maps
  to February 28 in a non-leap year.

Explicit JSON offsets normalize to UTC; timezone-free input and service
`Unspecified` values assume UTC. Service `Local` values are rejected. Query parsing
is invariant; encode positive-offset `+` as `%2B`.

Invalid submissions return 400 with field errors. A missing referenced cover is a
`coverId` validation error; missing-resource GET/DELETE returns 404. Successful
operations return 200, DELETE has an empty body, and empty lists return `[]`.
Compatibility retains required create-body `id` placeholders, replaced by generated
GUIDs, and required claim `name`. Cover premiums are calculated server-side.

## Premium calculation

[PremiumCalculator](../Claims.Domain/Services/PremiumCalculator.cs) uses a base daily
rate of 1250, multiplied by 1.10 for Yacht, 1.20 for PassengerShip, 1.50 for Tanker,
and 1.30 for ContainerShip, BulkCarrier, or other enum values.

| Billable days | Yacht rate | Other types rate |
| --- | --- | --- |
| 1–30 | 100% | 100% |
| 31–180 | 95% | 98% |
| 181 onward | 92% | 97% |

Discounts accumulate as percentage points: 5% + 3% = 8% for Yacht and 2% + 1% = 3%
for others. Tier totals use `decimal`; a 200-day Yacht costs
`30 × 1375 + 150 × 1375 × 0.95 + 20 × 1375 × 0.92 = 262487.50`.

Positive partial 24-hour periods round up, even by one tick. There is no extra
inclusive end-date charge or 365-day billing cap. No monetary rounding policy or
recalculation of existing premiums is introduced. Invalid periods return HTTP 400;
direct calculator calls throw `ArgumentOutOfRangeException`.

## Asynchronous auditing

The flow is MongoDB mutation → queue acceptance → HTTP response.
[AuditWorker](../Claims.Infrastructure/Auditing/AuditWorker.cs) writes SQL separately.
Successful POST/DELETE operations append history rows; DELETE preserves history.
Reads, quotes, invalid requests, and failed MongoDB operations publish no audit.

The in-memory queue holds 1000 events and waits when full, potentially delaying
requests. SQL writes receive three attempts with 1- and 2-second retry delays;
exhausted events are logged and abandoned. Shutdown attempts to drain accepted
events until the host deadline; SQL cancellation is cooperative.

Delivery is best-effort: crashes or shutdown deadlines can lose events; MongoDB
writes and enqueueing are not atomic; enqueue failure does not roll back data.
Ambiguous SQL commits can produce duplicates. There is no durable replay or
exactly-once guarantee. Existence checks can race with deletion. Audit timestamps
use local wall-clock `datetime2`, making timezone/DST interpretation ambiguous.

## Tests and known limitations

Tests cover validation, pricing boundaries, failure ordering, DTO/JSON mapping,
project dependencies, and DI lifetimes. Controlled writers demonstrate HTTP
completion before SQL persistence. Docker-backed tests verify BSON mappings,
SQL migration history/model consistency, audit rows, and production-host
Swagger/CRUD behavior.

From the repository root, after `dotnet restore`:

```sh
# Docker-free unit, component, and HTTP tests
dotnet test --no-restore --filter-class 'Claims.Tests.Unit.*'
# Complete suite, including Docker-backed tests
dotnet test --no-restore
```

The runner is Microsoft.Testing.Platform, configured in `global.json`.

**MongoDB precision:** BSON DateTime loses sub-millisecond ticks on reload. A
one-tick cover can become zero-duration; a claim at the original end can fail
against the reloaded boundary. Input is neither rejected nor normalized to storage
precision. [Precision tests](../Claims.Tests/Integration/MongoDateTimePrecisionTests.cs)
reproduce this limitation; millisecond-aligned values round-trip exactly.

**Dependency warnings:** NU1902/NU1903 remain for transitive
`System.Security.Cryptography.Pkcs 9.0.11` via `Microsoft.Data.SqlClient`, covering
Moderate/High denial-of-service advisories. Direct application exploitability has
not been established. Temporary container data should not be expected to survive
application restarts.
