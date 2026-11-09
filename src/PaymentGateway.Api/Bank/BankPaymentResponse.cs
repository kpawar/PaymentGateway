using System.Text.Json.Serialization;

namespace PaymentGateway.Api.Bank;

public record BankPaymentResponse([property: JsonRequired] bool Authorized, string? AuthorizationCode);
