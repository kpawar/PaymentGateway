# Plan: Payment Gateway API, built test-first

> **Progress (update as work proceeds)**
> - TDD step 1 (tooling): done. Packages upgraded, `public partial class Program` added.
> - TDD step 2 (repository): done. 10 unit tests in `Unit/Persistence/InMemoryPaymentsRepositoryTests.cs` pass against `InMemoryPaymentsRepository`, which implements `IPaymentsRepository`.
> - TDD step 3 (GET endpoint): done. Integration tests in `Integration/PaymentsControllerTests.cs` cover 404 and a seeded payment (masked `PaymentResponse`, status as string). Old boilerplate repository and response DTOs removed. Tidy-up: `PaymentStatus` moved to `Domain/` (namespace `PaymentGateway.Api.Domain`), stale usings removed, factories disposed. `Failed` status is added later in step 6.
> - TDD step 4 (validator): done. `PostPaymentRequest` reshaped (nullable, `CardNumber` and `Cvv` as strings). `PostPaymentRequestValidator` covers card number, expiry month, expiry (year and month vs `TimeProvider`, reported on the combined `ExpiryDate` field), currency, amount and CVV. Messages never echo card data. The planned all-null request test was skipped, because each field already has its own null case.
> - TDD step 5 (bank client): done. `AcquiringBankClient` posts a snake_case body with `MM/YYYY` expiry to `/payments` and returns `BankPaymentResponse(Authorized, AuthorizationCode)`. A non-success status (including 400), a connection error and a timeout all throw `AcquiringBankUnavailableException`; a cancellation by the caller propagates unchanged. Tested with `StubHttpMessageHandler`. `IAcquiringBankClient` was added in step 6 and is implemented by `AcquiringBankClient`; its options and typed `HttpClient` registration were added in step 7.
> - TDD step 6 (`PaymentService`): done. 6a: `Services/RequestFingerprinter` computes a secret-keyed HMAC-SHA256 over card number, expiry, currency and amount (CVV excluded), tolerating missing fields. 6b: invalid requests return `PaymentResult.Rejected(PaymentId, Errors)` and are stored as `Rejected` with fingerprint, `RejectionReasons` and timestamps; full card number and CVV are not stored. 6c: valid requests are sent through `IAcquiringBankClient` and stored as `Authorized` (including authorization code) or `Declined`; bank unavailability stores `Failed` and returns `PaymentResult.BankUnavailable`. Rejected requests do not call the bank. For rejected attempts, last four digits are kept only if the supplied card number is all digits and at least four characters long. 6d: matching idempotency keys and fingerprints replay the original outcome (including validation errors or bank failure), different fingerprints return `PaymentResult.IdempotencyConflict`, and CVV-only changes replay because CVV is excluded from the fingerprint. 6e: the service atomically reserves a `Pending` payment via `TryAddAsync` before validation or bank access, then updates it with its final outcome; matching in-flight duplicates return `InProgress`. Concurrent duplicate requests are covered by a test asserting the bank is called once. The focused `PaymentServiceTests` suite passes (20 tests).
> - TDD step 7 (POST endpoint): done. POST integration tests were written first and verified red before implementation. `PaymentsController` now maps all service results to the HTTP contract, `IPaymentService` defines the boundary, and `Program.cs` registers the validator, `TimeProvider`, fingerprinter, service, and typed bank client. `AcquiringBankOptions` configures the bank URL and timeout; `RequestFingerprint:Secret` (environment variable `RequestFingerprint__Secret`) is required and validated as at least 32 bytes. Integration tests cover authorized, declined, rejected, failed, replay, conflict, in-progress, invalid/missing keys, malformed JSON, and POST followed by GET for all stored statuses. Rejected and failed attempts retain supplied expiry, currency, and amount metadata for GET. All 110 non-simulator tests pass.
> - Decision change since the plan was first written: fingerprint uses HMAC-SHA256 without the CVV (see POST flow and assumption 8).

## Context
The repo is the Checkout.com .NET payment gateway challenge skeleton. The spec (cko-recruitment README) asks for two things:
1. **POST a payment.** Validate it. If it's invalid, return **Rejected** and don't call the bank. If it's valid, call the acquiring bank simulator (Mountebank on `localhost:8080/payments`) and return **Authorized** or **Declined**, the id, the last four digits only, expiry month and year, currency and amount.
2. **GET a payment by id.** Return the same masked details.

The plan has to stay simple and readable, be test-driven (red, green, refactor) and leave room to scale without over-engineering.

### Decisions confirmed by you
| Topic | Decision |
|---|---|
| Invalid request | `400`, **and the attempt is stored** (status `Rejected`) for reporting |
| Bank unavailable | `502`, **and the attempt is stored** (status `Failed`) |
| Successful POST | `200 OK` (Authorized or Declined) |
| Idempotency | In scope. The `Idempotency-Key` header must hold a GUID |
| Expiry | A card expiring in the current month is valid |
| Currencies | GBP, USD, EUR |
| Structure | One API project plus one test project |
| Merchant authentication | Out of scope |
| Libraries | FluentValidation, Shouldly, NSubstitute (plus xUnit and `Microsoft.AspNetCore.Mvc.Testing`) |

### Problems found in the original boilerplate (all fixed by step 5, each driven by a test)
- `PaymentsController.GetPaymentAsync` returned `200` with a `null` body when nothing was found, so `Returns404IfPaymentNotFound` failed. The method was also marked `async` but never awaited anything.
- `PostPaymentRequest` had `CardNumberLastFour` (an int) and no full card number. `Cvv` was an int, and so was `CardNumberLastFour` in the responses, so leading zeros were lost (for example "0123" becomes 123). These all became `string`.
- `PaymentsRepository` was a concrete class holding a public, non-thread-safe `List`, and it stored the *response* DTO. It is now an interface backed by `ConcurrentDictionary`s that store a domain `Payment`.
- `PaymentStatus` was in `Enums/` but its namespace was `PaymentGateway.Api.Models`. It now lives in `Domain/`.
- The test project referenced `Microsoft.AspNetCore.Mvc.Testing 6.0.24` while targeting net8, and xUnit and the test SDK were old. All upgraded.
- The existing test used `_random.Next(1, 12)`, which never produces 12. Its test data is now deterministic.

## Architecture (one project, layered by folder, every boundary behind an interface)

```
src/PaymentGateway.Api/
  Controllers/PaymentsController.cs          POST: header/body -> service -> HTTP result; GET reads the repository directly
  Models/Requests/PostPaymentRequest.cs      CardNumber, ExpiryMonth, ExpiryYear, Currency, Amount, Cvv (string for digit fields, nullable)
  Models/Responses/PaymentResponse.cs        Id, Status, CardNumberLastFour, ExpiryMonth, ExpiryYear, Currency, Amount
  Domain/Payment.cs                          stored entity (see below)
  Domain/PaymentStatus.cs                    Pending, Authorized, Declined, Rejected, Failed
  Validation/PostPaymentRequestValidator.cs  FluentValidation + TimeProvider (done)
  Services/RequestFingerprinter.cs           (done) HMAC-SHA256 fingerprint, CVV excluded
  Services/IPaymentService.cs, PaymentService.cs     idempotency -> validate -> bank -> persist
  Services/RequestFingerprintOptions.cs       configured HMAC secret (required; minimum 32 bytes)
  Services/PaymentResult.cs                  outcome type: Processed | Rejected(errors) | BankUnavailable | IdempotencyConflict
  Persistence/IPaymentsRepository.cs, InMemoryPaymentsRepository.cs
  Bank/AcquiringBankClient.cs (done; owns the private snake_case wire record and the MM/YYYY formatting),
  Bank/BankPaymentRequest.cs (plain input: ExpiryMonth, ExpiryYear as ints), Bank/BankPaymentResponse.cs (done)
  Bank/IAcquiringBankClient.cs               added in step 6, when the service needs to substitute it
  Bank/AcquiringBankOptions.cs               BaseUrl, Timeout (appsettings "AcquiringBank")
  Bank/AcquiringBankUnavailableException.cs  (done)
  Program.cs                                  DI, validated options, typed HttpClient, TimeProvider.System, JsonStringEnumConverter
test/PaymentGateway.Api.Tests/
  Unit/         validator, service, bank client, repository
  Integration/  WebApplicationFactory<Program> with a substituted IAcquiringBankClient
  EndToEnd/     [Trait("Category","BankSimulator")], runs against docker-compose
```

**Domain `Payment`** stores `Id`, `IdempotencyKey`, `RequestFingerprint`, `Status`, `CardNumberLastFour` (nullable), `ExpiryMonth`, `ExpiryYear`, `Currency`, `Amount`, `AuthorizationCode`, `RejectionReasons`, `CreatedAt` and `UpdatedAt`. It **never** holds the full card number or the CVV.

**Each piece is there for one of two reasons:**
- **To scale or be swapped later:**
  - `IPaymentsRepository` can be replaced by SQL, Cosmos or similar. Its unique index on the idempotency key maps directly onto a DB unique constraint.
  - `IAcquiringBankClient` allows several acquirers or routing later.
  - Everything is async with a `CancellationToken`.
  - The service is stateless, so the API scales horizontally once the repository is shared.
  - The typed `HttpClient` goes through `IHttpClientFactory`.
- **To be testable:** the clock is injected through `TimeProvider` and faked with `FakeTimeProvider`. The bank is substituted with NSubstitute, and the HTTP layer is tested with a stub `HttpMessageHandler`.

### POST flow (`PaymentService.ProcessAsync(idempotencyKey, request, ct)`)
1. Compute the **fingerprint**: an **HMAC-SHA256** (server-side secret key from configuration) over the normalised card number, expiry month/year, currency and amount. The **CVV is excluded** (PCI DSS forbids retaining it) and the raw card number is only hashed, never stored. A plain SHA-256 is not used because card numbers can be brute-forced.
2. **Reserve the key atomically** with `repository.TryAddAsync(new Payment { Status = Pending, ... })`. The in-memory version uses `ConcurrentDictionary.TryAdd` keyed by the idempotency key.
   - If the key already exists:
     - The stored fingerprint differs → `IdempotencyConflict`, returned as **`422`**.
     - The stored status is still `Pending` (a duplicate request still in flight) → **`409 Conflict`**.
     - Otherwise → replay the stored outcome with the same status code and body. No bank call.
3. **Validate.** If invalid, update to `Rejected` with the reasons. Return **`400`** `ValidationProblemDetails` with extensions `paymentId` and `status: "Rejected"`. The bank is not called.
4. **Call the bank.** A 200 response with `authorized: true/false` → update to `Authorized` (with the authorization code) or `Declined` → **`200`** `PaymentResponse`.
   - A 5xx, timeout or connection error → update to `Failed` → **`502`** ProblemDetails with `paymentId`.
   - A 400 from the bank (a gateway bug, since our validation should have caught it) is also treated as `Failed`.
5. **Last four for rejected attempts:** stored only if the card number supplied is all digits and at least 4 long. Otherwise `null`.

### Idempotency header
- `Idempotency-Key` is required on POST and must parse as a GUID.
- If it's missing or malformed → `400` ProblemDetails. This is a protocol error with no payment context, so it is **not** stored.
- Same goes for JSON that won't deserialize (for example `"amount": "abc"`): the automatic `[ApiController]` 400 handles it and nothing is stored. Request fields are nullable so that *missing* fields still reach the validator and are stored as Rejected.

### HTTP contract
| Case | Response | Stored as |
|---|---|---|
| Valid, bank authorizes | `200` `{ id, status: "Authorized", cardNumberLastFour, expiryMonth, expiryYear, currency, amount }` | Authorized |
| Valid, bank declines | `200`, `status: "Declined"` | Declined |
| Invalid fields | `400` ValidationProblemDetails + `paymentId`, `status: "Rejected"` | Rejected |
| Bank 5xx / timeout | `502` ProblemDetails + `paymentId` | Failed |
| Replay (same key, same body) | same code and body as the original | — |
| Same key, different body | `422` ProblemDetails | — |
| Same key, still processing | `409` ProblemDetails | — |
| Missing / non-GUID key | `400` ProblemDetails | not stored |
| GET existing id (any status) | `200` PaymentResponse | — |
| GET unknown id | `404` ProblemDetails | — |

### Validation rules (FluentValidation)
- Card number: 14 to 19 characters, digits only.
- Expiry month: 1 to 12.
- Expiry: `(year, month) >= (now.Year, now.Month)` from `TimeProvider`. Reported on a combined `ExpiryDate` field, and only checked when the month (1 to 12) and year are present, so a bad month gives only the month error.
- Currency: one of `GBP`, `USD`, `EUR` (case-sensitive).
- Amount: greater than 0.
- CVV: 3 or 4 characters, digits only.
- Every field is required.
- Messages are fixed strings and never echo the attempted value, so card data cannot leak into responses or logs.

### Bank mapping
- The request is `{ card_number, expiry_date: "MM/YYYY", currency, amount, cvv }`.
- The response is `{ authorized, authorization_code }`.
- Any non-success response (including a 400), an `HttpRequestException` or a timeout throws `AcquiringBankUnavailableException`. A cancellation by the caller's own token propagates unchanged.
- No automatic retries, to avoid double charges. A configurable `HttpClient.Timeout` applies.

## TDD sequence (each step: write a failing test → minimal code → refactor)
1. **Tooling.** (done) Upgrade the test packages, add FluentValidation, Shouldly, NSubstitute and `Microsoft.Extensions.TimeProvider.Testing`, and add `public partial class Program`. `Returns404` stays red.
2. **Repository.** (done) Unit tests:
   - `TryAdd` succeeds the first time and fails for a duplicate key
   - get by id and get by idempotency key
   - update
   - parallel `TryAdd` with the same key → exactly one wins
3. **GET.** (done) Integration tests: an unknown id returns 404 (turning the existing test green), and a seeded payment returns 200 with all fields and the last four digits only.
4. **Validator.** (done) Theory-based unit tests per rule at the boundaries: card number length 13/14/19/20 and non-digits; month 0/1/12/13; last month, this month and next month with `FakeTimeProvider`; currency outside the list or lowercase; amount 0 or -1; CVV of 2/3/4/5 characters or non-digits; null fields.
5. **Bank client.** (done) Stub-handler tests:
   - the snake_case body and the `MM/YYYY` date format
   - authorized maps to Authorized, unauthorized maps to Declined
   - 503, 400, a timeout and a connection error all throw `AcquiringBankUnavailableException`
6. **PaymentService.** Unit tests:
   - Rejected is stored, the bank is not called, and the reasons are kept
   - Authorized or Declined is stored with the authorization code
   - a bank failure is stored as Failed
   - the full card number and CVV never appear on `Payment`
   - idempotency: replay returns the same result and the bank is called once; a different fingerprint gives a conflict; a Pending payment gives in-progress
7. **POST endpoint.** (done) Integration tests cover every row in the contract table, plus POST followed by GET for Authorized, Declined, Rejected and Failed payments.
8. **End-to-end.** Run against the simulator with `docker-compose up`, using card numbers ending in 1 (200 Authorized), 2 (200 Declined) and 0 (502, retrievable as Failed).
9. **Refactor and document.**
   - Exception handling via `AddProblemDetails` and `UseExceptionHandler`.
   - Swagger: document the `Idempotency-Key` header and the response types.
   - README section on design decisions and assumptions: the hashed fingerprint, the global idempotency-key scope (no merchant auth), no retries, the extra `Pending` and `Failed` statuses, and the in-memory store being per-instance.

## Assumptions
1. Merchant authentication is out of scope, so idempotency keys are unique across all merchants. Once auth exists, scope them to (merchantId, key).
2. Payment statuses are `Authorized`, `Declined` and `Rejected` from the spec, plus two internal ones: `Pending` (reserved, bank call in progress) and `Failed` (bank unavailable). All are retrievable via GET.
3. Every attempt that reaches the service is stored for reporting: Rejected, Failed, Authorized and Declined.
4. A missing or non-GUID `Idempotency-Key` and JSON that won't deserialize are protocol errors. They return `400` and are not stored.
5. Missing required fields are a validation failure. They are stored as Rejected.
6. Replaying a key always returns the original outcome, including a `400` or `502`. To retry after a `502`, the merchant uses a new key. The gateway never retries the bank automatically, to avoid double charges.
7. Same key with a different body → `422`. Same key while the first request is still `Pending` → `409`.
8. The request fingerprint is an HMAC-SHA256 (keyed with `RequestFingerprint:Secret`, configured outside source control and validated to be at least 32 bytes) over card number, expiry, currency and amount; the CVV is excluded. The full card number and CVV are never stored or logged.
9. For a Rejected attempt, the last four digits are stored only if the card number supplied is all digits and at least 4 long.
10. Card expiry is valid through the end of its expiry month, so the current month is valid.
11. Currency must be one of GBP, USD or EUR (uppercase, case-sensitive). Amount is an integer in minor units and must be greater than 0.
12. A `400` from the bank means a request the gateway built wrongly. It is treated as `Failed` and returned to the merchant as `502`.
13. The in-memory repository is per-instance and lost on restart. Running multiple instances needs a shared store with a unique constraint on the idempotency key. The interface is designed for that.
14. The API lives in one project, organised by folders. `.editorconfig`, `imposters/` and `docker-compose.yml` are not changed.

## Critical files
- Modify:
  - `src/PaymentGateway.Api/Program.cs`
  - `Controllers/PaymentsController.cs`
  - `Models/Requests/PostPaymentRequest.cs`
  - `Models/Responses/*` (merge into `PaymentResponse`)
  - `Enums/PaymentStatus.cs` (moves to `Domain/`)
  - `Services/PaymentsRepository.cs` (moves to `Persistence/`)
  - `appsettings*.json` (`AcquiringBank:BaseUrl = http://localhost:8080`)
  - both `.csproj` files
  - `test/.../PaymentsControllerTests.cs` (moves to `Integration/`)
- Don't touch: `.editorconfig`, `imposters/`, `docker-compose.yml`.

## Verification
- `dotnet build` and `dotnet test --filter "Category!=BankSimulator"` pass with no running dependencies.
- `docker-compose up -d`, then `dotnet test --filter Category=BankSimulator` passes.
- Manual check: configure `RequestFingerprint__Secret` with a secret of at least 32 bytes, then run `dotnet run --project src/PaymentGateway.Api` and use curl or Swagger:
  - POST cards ending in 1, 2 and 0
  - POST an invalid payload
  - resend the same key and body → identical response
  - same key with a different amount → 422
  - GET each returned id
- `dotnet test --collect:"XPlat Code Coverage"` to check coverage.

## Known limitations
- A payment left `Pending` by a crash between the bank call and `UpdateAsync` stays `Pending`, so its key keeps returning `409`. A real store needs a timeout or reconciliation job.
- `InMemoryPaymentsRepository` updates are not atomic across its two indexes; a database would use one transactional update (ideally with a version check).
