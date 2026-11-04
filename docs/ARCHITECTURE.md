# Architecture and reviewer guide

## Where to start

- [ClaimService](../Claims/Services/ClaimService.cs) and
  [CoverService](../Claims/Services/CoverService.cs): validation and mutation ordering.
- [PremiumCalculator](../Claims/Services/PremiumCalculator.cs) and its
  [tests](../Claims.Tests/Unit/PremiumCalculatorTests.cs): pricing rules and boundary examples.
- [AuditWorker](../Claims/Auditing/AuditWorker.cs): retries, scope ownership, and shutdown.
- [MongoDateTimePrecisionTests](../Claims.Tests/Integration/MongoDateTimePrecisionTests.cs):
  the confirmed persistence limitation described below.

## Structure and dependency injection

Controllers bind requests and call services. Services coordinate validation, IDs,
pricing, repositories, and auditing. Repositories own MongoDB access through
`ClaimsContext`. Exception filters translate validation and missing-resource
exceptions into HTTP errors. The API stays in one project with shared request
and persistence models.

Services, repositories, and `ClaimsContext` are scoped. The stateless calculator,
`TimeProvider`, audit publisher, and queue are singletons. The hosted worker
resolves scoped `IAuditWriter` and `AuditContext` instances in a fresh async scope
per attempt, without capturing request-scoped contexts.
[Program.cs](../Claims/Program.cs) contains the registrations and Testcontainers startup.

Tasks 1–5 preserve mappings and migrations. Earlier setup changed audit model
nullability and migration metadata relative to the initial repository.

## Validation and HTTP behavior

Validation finishes before assigning IDs, saving, or publishing audits:

- Claim `DamageCost` must be between 0 and 100000, inclusive.
- `CoverId` must be nonblank and identify an existing cover.
- Claim `Created` must fall within the cover's start/end timestamps, inclusively.
- Cover `StartDate` must be today in UTC or later; any time today is accepted.
- `EndDate` must be strictly after `StartDate` and at most `StartDate.AddYears(1)`.
  The same rules apply to premium quotes.

Invalid submissions return 400 `ValidationProblemDetails` with field errors.
A missing referenced cover is a `coverId` error; GET/DELETE of a missing resource
returns 404 `ProblemDetails`. Successful creates, reads, quotes, and deletes return
200; deletes have an empty body and empty collections return `[]`.
Shared models retain implicit required fields: create requests still need an `id`
placeholder, which the service replaces, and claims require `name`.

Query parsing uses invariant .NET date parsing and accepts more formats than JSON.
Encode a positive offset's `+` as `%2B`. The date assumptions and storage precision
limits are described below.

## Asynchronous auditing

The mutation flow is MongoDB write → queue acceptance → HTTP response. The single
`AuditWorker` consumes events and writes SQL independently. DELETE now publishes
only after deletion completes, so a failed deletion does not publish a successful
audit. Invalid creates, missing resources, and failed MongoDB writes do not publish.
Reads and premium quotes are not audited. Both POST and DELETE events insert new
history rows; DELETE never removes audit history.

Events contain entity type, ID, operation, and publication timestamp. Audit dates
retain local wall-clock time in SQL `datetime2`, unlike UTC business dates; they
can be ambiguous across timezone or DST changes.

Writes get at most three attempts, with 1- and 2-second delays and a fresh scope
each time. Failures are logged with event identity and attempt number. Exhausted
events are abandoned. Shutdown closes publication and attempts to drain accepted
events until the host deadline cancels processing; SQL cancellation is cooperative.

## Premium calculation

The base daily rate is `1250m`, multiplied by 1.10 for Yacht, 1.20 for PassengerShip,
1.50 for Tanker, and 1.30 for ContainerShip, BulkCarrier, or other enum values.
Each day belongs to one tier:

| Days | Yacht rate | Other types rate |
| --- | --- | --- |
| 1–30 | 100% | 100% |
| 31–180 | 95% | 98% |
| 181 onward | 92% | 97% |

Tier premiums are summed using `decimal` arithmetic.
For example, a 200-day Yacht costs
`30 × 1375 + 150 × 1375 × 0.95 + 20 × 1375 × 0.92 = 262487.50`.

There is no 365-day cap: valid 366-day covers charge every day. Direct calls with
`endDate <= startDate` throw `ArgumentOutOfRangeException`; service validation
continues to return HTTP 400. No monetary rounding policy or recalculation of
previously stored premiums was introduced.

## Design decisions, trade-offs, and assumptions

- **Service/repository layering.** Services own business operations and repositories
  own persistence. This separates responsibilities and lets tests replace database
  access with fakes, at the cost of additional interfaces and classes. Request and
  persistence models remain shared, including the create-body `id` requirement.

- **Service-level validation.** Ordinary checks keep business rules in the services
  used by both creation and premium quoting, without a validation framework.
  Field errors are assembled manually; new rules must be maintained alongside
  their tests and HTTP error mapping.

- **Bounded in-memory auditing.** A shared `Channel<AuditEvent>` with capacity 1000
  moves SQL persistence out of the request flow without external messaging
  infrastructure. It waits when full, so backpressure can delay requests.
  Crashes and shutdown deadlines can lose accepted events; there is no durable replay.

- **Retries without distributed transactions.** Limited retries handle temporary
  failures while allowing processing to continue after an event is abandoned.
  Delivery is best-effort: MongoDB writes and audit publication are not atomic,
  enqueue failure does not roll back data, and ambiguous SQL commits can create
  duplicate audits. Existence checks also race with concurrent deletion. There is
  no exactly-once guarantee.

- **Fixed pricing tiers.** Three explicit tier calculations make the assignment's
  formula readable without pricing strategies or configuration infrastructure.
  Rates and thresholds are defined in code, so pricing changes require a code
  change and deployment.

- **Partial-day billing.** The assignment does not specify partial days. Positive
  partial elapsed 24-hour periods round up to full billable days, using integer
  ticks. There is no extra inclusive end-date charge. This is predictable but
  charges even one tick as a whole day rather than prorating it.

- **Progressive discounts.** “Additional” means cumulative percentage points:
  Yacht's final discount is 5% + 3% = 8%, and other types receive 2% + 1% = 3%.
  Compounded percentages would produce different totals and are not used.

- **Date assumptions.** Business dates use UTC so validation does not depend on
  the server timezone. Explicit JSON offsets normalize to UTC; timezone-free dates
  and service `Unspecified` values are assumed UTC, while service `Local` values
  are rejected. Clients must supply an offset for another timezone. Claim boundaries
  are inclusive exact timestamps, not whole calendar dates. Cover limits use a
  calendar-year anniversary: `AddYears(1)` preserves time of day, can span 366 days,
  and maps February 29 to February 28 in a non-leap year.

- **MongoDB precision — confirmed and unfixed.** Existing mappings store
  `Cover.StartDate`, `Cover.EndDate`, and `Claim.Created` as BSON DateTime with
  millisecond precision. Retaining those mappings leaves sub-millisecond ticks
  lost on reload: a one-tick cover becomes a zero-duration cover, and a claim at
  the original cover end can be rejected against the earlier reloaded boundary.
  Millisecond-aligned values round-trip exactly in the control test. The current
  code neither rejects nor normalizes sub-millisecond input; UTC handling does
  not fix this storage limitation.

## Tests and remaining warnings

Unit tests use fake repositories and a fixed clock for validation, operation
ordering, dependency failures, and independently calculated premium expectations.
Docker-free HTTP tests exercise MVC binding, filters, and serialization. Controlled
writers prove responses complete before SQL persistence is released.

Real SQL integration tests verify separate POST/DELETE audit rows and timestamps.
Real MongoDB tests inspect raw BSON and reload through a new context to demonstrate
precision loss. The original application smoke test exercises production startup
and claims-list access; full HTTP CRUD against both databases is not covered.
See the [README](../README.md#run-the-tests) for commands.

Restore/build reports unresolved NU1902/NU1903 warnings for transitive
`System.Security.Cryptography.Pkcs 9.0.11` via `Microsoft.Data.SqlClient`, covering
Moderate/High denial-of-service advisories. Direct application exploitability has
not been established.
