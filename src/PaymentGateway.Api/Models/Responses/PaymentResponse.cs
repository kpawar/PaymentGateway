using PaymentGateway.Api.Domain;

namespace PaymentGateway.Api.Models.Responses;

public record PaymentResponse
{
    public Guid Id { get; init; }
    public PaymentStatus Status { get; init; }
    public string? CardNumberLastFour { get; init; }
    public int? ExpiryMonth { get; init; }
    public int? ExpiryYear { get; init; }
    public string? Currency { get; init; }
    public int? Amount { get; init; }

    public static PaymentResponse From(Payment payment) => new()
    {
        Id = payment.Id,
        Status = payment.Status,
        CardNumberLastFour = payment.CardNumberLastFour,
        ExpiryMonth = payment.ExpiryMonth,
        ExpiryYear = payment.ExpiryYear,
        Currency = payment.Currency,
        Amount = payment.Amount
    };
}
