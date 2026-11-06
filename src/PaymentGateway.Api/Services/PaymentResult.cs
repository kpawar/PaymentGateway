using PaymentGateway.Api.Domain;

namespace PaymentGateway.Api.Services;

/// <summary>
/// The outcome of processing a payment request, for the controller to map to HTTP.
/// </summary>
public abstract record PaymentResult
{
    /// <summary>The bank answered: the payment is Authorized or Declined.</summary>
    public sealed record Processed(Payment Payment) : PaymentResult;

    /// <summary>The request failed validation. Errors are keyed by property name.</summary>
    public sealed record Rejected(Guid PaymentId, IReadOnlyDictionary<string, string[]> Errors) : PaymentResult;

    /// <summary>The idempotency key was already used with a different request body.</summary>
    public sealed record IdempotencyConflict : PaymentResult;

    /// <summary>The same request is still being processed.</summary>
    public sealed record InProgress : PaymentResult;

    /// <summary>The bank could not be reached. The attempt is stored as Failed.</summary>
    public sealed record BankUnavailable(Guid PaymentId) : PaymentResult;
}
