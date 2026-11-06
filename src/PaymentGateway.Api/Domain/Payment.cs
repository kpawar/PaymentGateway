
namespace PaymentGateway.Api.Domain;

/// <summary>
/// A stored payment attempt. Never holds the full card number or the CVV.
/// </summary>
public record Payment
{
    public required Guid Id { get; init; }
    public required Guid IdempotencyKey { get; init; }
    public required string RequestFingerprint { get; init; }
    public required PaymentStatus Status { get; init; }
    public string? CardNumberLastFour { get; init; }
    public int? ExpiryMonth { get; init; }
    public int? ExpiryYear { get; init; }
    public string? Currency { get; init; }
    public int? Amount { get; init; }
    public string? AuthorizationCode { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}
