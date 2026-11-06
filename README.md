# Instructions for candidates

This is the .NET version of the Payment Gateway challenge. If you haven't already read this [README.md](https://github.com/cko-recruitment/) on the details of this exercise, please do so now. 

## Template structure
```
src/
    PaymentGateway.Api - a skeleton ASP.NET Core Web API
test/
    PaymentGateway.Api.Tests - an empty xUnit test project
imposters/ - contains the bank simulator configuration. Don't change this

.editorconfig - don't change this. It ensures a consistent set of rules for submissions when reformatting code
docker-compose.yml - configures the bank simulator
PaymentGateway.sln
```

Feel free to change the structure of the solution, use a different test library etc.

## Design decisions and assumptions

- **Request fingerprint:** Idempotency compares requests using an HMAC-SHA256 fingerprint of the card number, expiry, currency, and amount. The CVV is excluded and is never stored. Configure `RequestFingerprint__Secret` outside source control; it must contain at least 32 bytes.
- **Idempotency-key scope:** Keys are global because merchant authentication is out of scope. If merchant authentication is added, scope keys to the merchant and key.
- **Bank retries:** The gateway does not automatically retry bank requests, to avoid duplicate charges. After a stored `Failed` result, a merchant must use a new key to make another attempt.
- **Payment statuses:** `Pending` tracks an in-flight request and `Failed` records bank unavailability, in addition to `Authorized`, `Declined`, and `Rejected`. These statuses can be retrieved with GET.
- **Persistence:** Payments are held in an in-memory, per-instance store and are lost on restart. Multiple API instances require a shared store with an idempotency-key uniqueness constraint.