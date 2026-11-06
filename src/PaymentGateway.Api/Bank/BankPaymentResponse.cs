namespace PaymentGateway.Api.Bank;

public record BankPaymentResponse(bool Authorized, string? AuthorizationCode);
