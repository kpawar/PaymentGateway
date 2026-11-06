namespace PaymentGateway.Api.Models.Requests;

/// <summary>
/// Fields are nullable so that missing values reach the validator instead of failing model binding.
/// Digit-only fields are strings to preserve leading zeros.
/// </summary>
public class PostPaymentRequest
{
    public string? CardNumber { get; set; }
    public int? ExpiryMonth { get; set; }
    public int? ExpiryYear { get; set; }
    public string? Currency { get; set; }
    public int? Amount { get; set; }
    public string? Cvv { get; set; }
}
