namespace PaymentGateway.Api.Bank;

public sealed class AcquiringBankOptions
{
    public const string SectionName = "AcquiringBank";

    public string BaseUrl { get; set; } = string.Empty;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
}
