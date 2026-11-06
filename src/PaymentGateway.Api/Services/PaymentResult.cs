namespace PaymentGateway.Api.Services;

/// <summary>
/// The outcome of processing a payment request, for the controller to map to HTTP.
/// </summary>
public abstract record PaymentResult
{
    /// <summary>The request failed validation. Errors are keyed by property name.</summary>
    public sealed record Rejected(Guid PaymentId, IReadOnlyDictionary<string, string[]> Errors) : PaymentResult;
}
