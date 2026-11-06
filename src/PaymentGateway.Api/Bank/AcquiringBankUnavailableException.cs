namespace PaymentGateway.Api.Bank;

public class AcquiringBankUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
