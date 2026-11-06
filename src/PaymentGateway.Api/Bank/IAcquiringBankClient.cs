namespace PaymentGateway.Api.Bank;

public interface IAcquiringBankClient
{
    /// <exception cref="AcquiringBankUnavailableException">The bank could not be reached or did not answer successfully.</exception>
    Task<BankPaymentResponse> AuthorizeAsync(BankPaymentRequest request, CancellationToken cancellationToken = default);
}
